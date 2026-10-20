using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using MediaMtxPanel.Data;
using MediaMtxPanel.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Services.Payments;

public class OfferInput
{
    [Required(ErrorMessage = "Give the offer a name."), StringLength(80)]
    public string Name { get; set; } = "";

    [StringLength(300)]
    public string? Description { get; set; }

    [Range(0.5, 10000, ErrorMessage = "Choose a price between €0.50 and €10,000.")]
    public decimal Price { get; set; } = 4.95m;

    [Range(1, 24 * 366, ErrorMessage = "Choose an access period of at least 1 hour.")]
    public int AccessHours { get; set; } = 24;

    public bool Active { get; set; } = true;
}

public class PaymentAccountInput
{
    public bool MollieEnabled { get; set; }
    /// <summary>New key; empty keeps the stored one.</summary>
    public string? MollieApiKey { get; set; }
    public bool HasMollieKey { get; set; }

    public bool PayPalEnabled { get; set; }
    public string? PayPalClientId { get; set; }
    /// <summary>New secret; empty keeps the stored one.</summary>
    public string? PayPalSecret { get; set; }
    public bool HasPayPalSecret { get; set; }
    public bool PayPalSandbox { get; set; } = true;
}

public record SalesSummary(int PaidCount, int RevenueCents, int ActiveTickets, List<TicketOrder> Recent);

public class AccessCodeInput
{
    [Range(1, 500, ErrorMessage = "Create between 1 and 500 codes at a time.")]
    public int Count { get; set; } = 1;

    [Range(1, 24 * 366, ErrorMessage = "Choose an access period of at least 1 hour.")]
    public int AccessHours { get; set; } = 24;

    /// <summary>Codes not used before this date stop working; empty means no deadline.</summary>
    public DateTime? RedeemBefore { get; set; }

    [StringLength(80)]
    public string? Label { get; set; }
}

public enum RedeemStatus { Ok, NotFound, Expired }

/// <summary>Offers, orders and tickets for paywalled streams.</summary>
public class PaywallService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IEnumerable<IPaymentGateway> gateways,
    IDataProtectionProvider dataProtection,
    IOptions<PanelOptions> panelOptions,
    ILogger<PaywallService> logger)
{
    private IDataProtector Protector => dataProtection.CreateProtector("MediaMtxPanel.PaymentSecrets.v1");

    public bool TestPaymentsEnabled => panelOptions.Value.EnableTestPayments;

    // ------------------------------------------------------------------ payment accounts

    public async Task<PaymentAccountInput> GetAccountInputAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.PaymentAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return a is null
            ? new PaymentAccountInput()
            : new PaymentAccountInput
            {
                MollieEnabled = a.MollieEnabled,
                HasMollieKey = !string.IsNullOrEmpty(a.MollieApiKeyProtected),
                PayPalEnabled = a.PayPalEnabled,
                PayPalClientId = a.PayPalClientId,
                HasPayPalSecret = !string.IsNullOrEmpty(a.PayPalSecretProtected),
                PayPalSandbox = a.PayPalSandbox,
            };
    }

    public async Task SaveAccountAsync(string userId, PaymentAccountInput input, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var a = await db.PaymentAccounts.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (a is null)
        {
            a = new PaymentAccount { UserId = userId };
            db.PaymentAccounts.Add(a);
        }
        a.MollieEnabled = input.MollieEnabled;
        if (!string.IsNullOrWhiteSpace(input.MollieApiKey))
            a.MollieApiKeyProtected = Protector.Protect(input.MollieApiKey.Trim());
        a.PayPalEnabled = input.PayPalEnabled;
        a.PayPalClientId = string.IsNullOrWhiteSpace(input.PayPalClientId) ? null : input.PayPalClientId.Trim();
        if (!string.IsNullOrWhiteSpace(input.PayPalSecret))
            a.PayPalSecretProtected = Protector.Protect(input.PayPalSecret.Trim());
        a.PayPalSandbox = input.PayPalSandbox;
        a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private PaymentCredentials Credentials(PaymentAccount? a) => new(
        Unprotect(a?.MollieApiKeyProtected),
        a?.PayPalClientId,
        Unprotect(a?.PayPalSecretProtected),
        a?.PayPalSandbox ?? true);

    private string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return Protector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The encryption keys changed (e.g. Panel__DataProtectionKeysPath not persisted); the owner must re-enter the secret.
            throw new PaymentException("The seller's payment credentials cannot be read. Ask the seller to enter them again.");
        }
    }

    /// <summary>Providers a viewer can pay with for streams owned by this user.</summary>
    public async Task<List<PaymentProvider>> ProvidersForOwnerAsync(string? ownerId, CancellationToken ct = default)
    {
        var list = new List<PaymentProvider>();
        if (ownerId is not null)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var a = await db.PaymentAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == ownerId, ct);
            if (a is { MollieEnabled: true, MollieApiKeyProtected.Length: > 0 })
                list.Add(PaymentProvider.Mollie);
            if (a is { PayPalEnabled: true, PayPalClientId.Length: > 0, PayPalSecretProtected.Length: > 0 })
                list.Add(PaymentProvider.PayPal);
        }
        if (TestPaymentsEnabled)
            list.Add(PaymentProvider.Test);
        return list;
    }

    // ------------------------------------------------------------------ offers

    public async Task<List<StreamOffer>> OffersAsync(int streamId, bool activeOnly, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Offers.AsNoTracking().Where(o => o.StreamId == streamId);
        if (activeOnly)
            q = q.Where(o => o.Active);
        return await q.OrderBy(o => o.SortOrder).ThenBy(o => o.PriceCents).ToListAsync(ct);
    }

    private static async Task<StreamDefinition?> ManagedStreamAsync(ApplicationDbContext db, ClaimsPrincipal user, int streamId, CancellationToken ct)
    {
        var s = await db.Streams.FirstOrDefaultAsync(x => x.Id == streamId, ct);
        return s is not null && StreamService.CanManage(user, s) ? s : null;
    }

    public async Task<bool> SaveOfferAsync(ClaimsPrincipal user, int streamId, int? offerId, OfferInput input, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await ManagedStreamAsync(db, user, streamId, ct) is null)
            return false;
        StreamOffer? offer;
        if (offerId is null)
        {
            offer = new StreamOffer { StreamId = streamId, SortOrder = await db.Offers.CountAsync(o => o.StreamId == streamId, ct) };
            db.Offers.Add(offer);
        }
        else
        {
            offer = await db.Offers.FirstOrDefaultAsync(o => o.Id == offerId && o.StreamId == streamId, ct);
            if (offer is null)
                return false;
        }
        offer.Name = input.Name.Trim();
        offer.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        offer.PriceCents = (int)Math.Round(input.Price * 100);
        offer.AccessHours = input.AccessHours;
        offer.Active = input.Active;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteOfferAsync(ClaimsPrincipal user, int streamId, int offerId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await ManagedStreamAsync(db, user, streamId, ct) is null)
            return false;
        var offer = await db.Offers.FirstOrDefaultAsync(o => o.Id == offerId && o.StreamId == streamId, ct);
        if (offer is null)
            return false;
        db.Offers.Remove(offer); // existing orders keep their copied name and price
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<string?> SetPaywallAsync(ClaimsPrincipal user, int streamId, bool enabled, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await ManagedStreamAsync(db, user, streamId, ct);
        if (s is null)
            return "Stream not found.";
        // Access codes alone are enough for a paywall: the owner hands them out instead of selling tickets.
        var hasCodes = await db.Orders.AnyAsync(o => o.StreamId == streamId && o.Provider == PaymentProvider.Manual && o.Status == OrderStatus.Paid, ct);
        if (enabled && !hasCodes)
        {
            if (!await db.Offers.AnyAsync(o => o.StreamId == streamId && o.Active, ct))
                return "Add at least one active offer first.";
            if ((await ProvidersForOwnerAsync(s.OwnerId, ct)).Count == 0)
                return "Set up a payment account first (iDEAL via Mollie, or PayPal).";
        }
        s.PaywallEnabled = enabled;
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<SalesSummary> SalesAsync(int streamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var paid = db.Orders.AsNoTracking().Where(o => o.StreamId == streamId && o.Status == OrderStatus.Paid && o.Provider != PaymentProvider.Manual);
        var now = DateTime.UtcNow;
        return new SalesSummary(
            await paid.CountAsync(ct),
            await paid.SumAsync(o => (int?)o.AmountCents, ct) ?? 0,
            await db.Orders.CountAsync(o => o.StreamId == streamId && o.Status == OrderStatus.Paid && o.AccessExpiresAt > now, ct),
            await db.Orders.AsNoTracking().Where(o => o.StreamId == streamId && o.Status != OrderStatus.Open && o.Provider != PaymentProvider.Manual)
                .OrderByDescending(o => o.CreatedAt).Take(20).ToListAsync(ct));
    }

    // ------------------------------------------------------------------ buying

    public async Task<StreamDefinition?> StreamBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Streams.AsNoTracking().Include(s => s.Server).FirstOrDefaultAsync(s => s.WatchSlug == slug, ct);
    }

    /// <summary>Creates an order and starts the payment. Returns the URL to send the buyer to.</summary>
    public async Task<string> StartPurchaseAsync(string slug, int offerId, PaymentProvider provider, string? email,
        string baseUrl, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stream = await db.Streams.AsNoTracking().FirstOrDefaultAsync(s => s.WatchSlug == slug, ct)
            ?? throw new PaymentException("Stream not found.");
        if (!stream.PaywallEnabled || !stream.Enabled)
            throw new PaymentException("Tickets are not sold for this stream.");
        var offer = await db.Offers.AsNoTracking().FirstOrDefaultAsync(o => o.Id == offerId && o.StreamId == stream.Id && o.Active, ct)
            ?? throw new PaymentException("This offer no longer exists.");
        if (!(await ProvidersForOwnerAsync(stream.OwnerId, ct)).Contains(provider))
            throw new PaymentException("This payment method is not available.");

        email = NormalizeEmail(email);
        if (email is null)
            throw new PaymentException("Enter your email address. You need it to sign in and find your ticket later.");

        var order = new TicketOrder
        {
            StreamId = stream.Id,
            Code = TicketCode.New(),
            OfferId = offer.Id,
            OfferName = offer.Name,
            AmountCents = offer.PriceCents,
            Currency = offer.Currency,
            AccessHours = offer.AccessHours,
            BuyerEmail = email,
            Provider = provider,
            AccessToken = CredentialGenerator.Password(32),
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        baseUrl = baseUrl.TrimEnd('/');
        var returnUrl = $"{baseUrl}/watch/{slug}/order/{order.Id}";
        var request = new CheckoutRequest(
            order,
            $"{stream.DisplayName}: {offer.Name}",
            returnUrl,
            returnUrl + "?canceled=true",
            IsPubliclyReachable(baseUrl) ? $"{baseUrl}/api/payments/mollie/webhook" : null);

        try
        {
            var account = await db.PaymentAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == stream.OwnerId, ct);
            var start = await Gateway(provider).StartAsync(request, Credentials(account), ct);
            order.ProviderPaymentId = start.ProviderPaymentId;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Order {Order} for stream {Stream} started with {Provider}", order.Id, stream.Name, provider);
            return start.CheckoutUrl;
        }
        catch (Exception ex) when (ex is PaymentException or HttpRequestException or TaskCanceledException)
        {
            order.Status = OrderStatus.Failed;
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Starting payment for order {Order} failed: {Message}", order.Id, ex.Message);
            throw ex as PaymentException ?? new PaymentException("The payment provider cannot be reached. Please try again later.");
        }
    }

    public async Task<TicketOrder?> GetOrderAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Orders.AsNoTracking().Include(o => o.Stream).FirstOrDefaultAsync(o => o.Id == id, ct);
    }

    /// <summary>Asks the provider for the latest status of an open order and records it.</summary>
    public async Task<TicketOrder?> RefreshOrderAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var order = await db.Orders.Include(o => o.Stream).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || order.Status != OrderStatus.Open || order.ProviderPaymentId is null)
            return order;
        try
        {
            var account = await db.PaymentAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == order.Stream!.OwnerId, ct);
            var status = await Gateway(order.Provider).CheckAsync(order, Credentials(account), ct);
            Apply(order, status);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is PaymentException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Checking order {Order} failed: {Message}", order.Id, ex.Message);
        }
        return order;
    }

    public async Task<Guid?> OrderIdForProviderPaymentAsync(PaymentProvider provider, string providerPaymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Orders.Where(o => o.Provider == provider && o.ProviderPaymentId == providerPaymentId)
            .Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>Completes a simulated payment (test provider only).</summary>
    public async Task<TicketOrder?> CompleteTestPaymentAsync(Guid id, CancellationToken ct = default)
    {
        if (!TestPaymentsEnabled)
            return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var order = await db.Orders.Include(o => o.Stream).FirstOrDefaultAsync(o => o.Id == id && o.Provider == PaymentProvider.Test, ct);
        if (order is { Status: OrderStatus.Open })
        {
            Apply(order, OrderStatus.Paid);
            await db.SaveChangesAsync(ct);
        }
        return order;
    }

    private void Apply(TicketOrder order, OrderStatus status)
    {
        if (status == order.Status)
            return;
        order.Status = status;
        if (status == OrderStatus.Paid)
        {
            order.PaidAt = DateTime.UtcNow;
            order.AccessExpiresAt = order.PaidAt.Value.AddHours(order.AccessHours);
            logger.LogInformation("Order {Order} paid: {Amount} for stream {Stream}",
                order.Id, Format.Money(order.AmountCents, order.Currency), order.StreamId);
        }
    }

    // ------------------------------------------------------------------ tickets

    /// <summary>The paid, unexpired order a ticket token belongs to, for this stream.</summary>
    public async Task<TicketOrder?> ValidTicketAsync(int streamId, string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        return await db.Orders.AsNoTracking().FirstOrDefaultAsync(o =>
            o.StreamId == streamId && o.AccessToken == token && o.Status == OrderStatus.Paid && o.AccessExpiresAt > now, ct);
    }

    /// <summary>A valid ticket of a signed-in viewer for this stream (the one that lasts longest).</summary>
    public async Task<TicketOrder?> ValidTicketForEmailAsync(int streamId, string? email, CancellationToken ct = default)
    {
        email = NormalizeEmail(email);
        if (email is null)
            return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        return await db.Orders.AsNoTracking()
            .Where(o => o.StreamId == streamId && o.BuyerEmail == email && o.Status == OrderStatus.Paid && o.AccessExpiresAt > now)
            .OrderByDescending(o => o.AccessExpiresAt).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Looks up a ticket code for a stream. An unused access code starts its access period now. When a
    /// viewer is signed in, a code without an owner is added to their tickets.
    /// </summary>
    /// <param name="streamId">The stream the code must belong to; null accepts a code of any stream (My tickets).</param>
    public async Task<(RedeemStatus Status, TicketOrder? Ticket)> RedeemCodeAsync(int? streamId, string? code, string? viewerEmail, CancellationToken ct = default)
    {
        code = TicketCode.Normalize(code);
        if (code.Length != TicketCode.Length)
            return (RedeemStatus.NotFound, null);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var order = await db.Orders.Include(o => o.Stream)
            .FirstOrDefaultAsync(o => o.Code == code && (streamId == null || o.StreamId == streamId), ct);
        if (order is null || order.Status is not OrderStatus.Paid)
            return (order is { Status: OrderStatus.Canceled } ? RedeemStatus.Expired : RedeemStatus.NotFound, null);

        var now = DateTime.UtcNow;
        if (order.IsUnusedCode)
        {
            if (order.RedeemBefore < now)
                return (RedeemStatus.Expired, null);
            order.PaidAt = now;
            order.AccessExpiresAt = now.AddHours(order.AccessHours);
            logger.LogInformation("Access code {Order} for stream {Stream} used for the first time", order.Id, order.StreamId);
        }
        if (!order.GivesAccess(now))
            return (RedeemStatus.Expired, null);
        if (order.BuyerEmail is null && NormalizeEmail(viewerEmail) is { } email)
            order.BuyerEmail = email;
        await db.SaveChangesAsync(ct);
        return (RedeemStatus.Ok, order);
    }

    /// <summary>Tickets of a viewer: valid ones first, then those that expired in the last 60 days.</summary>
    public async Task<List<TicketOrder>> TicketsForEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null)
            return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var since = DateTime.UtcNow.AddDays(-60);
        return await db.Orders.AsNoTracking().Include(o => o.Stream).ThenInclude(s => s!.Server)
            .Where(o => o.BuyerEmail == normalized && o.Status == OrderStatus.Paid && o.AccessExpiresAt > since)
            .OrderByDescending(o => o.AccessExpiresAt).ToListAsync(ct);
    }

    public async Task<bool> HasTicketsAsync(string email, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null)
            return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Orders.AnyAsync(o => o.BuyerEmail == normalized && o.Status == OrderStatus.Paid, ct);
    }

    // ------------------------------------------------------------------ access codes

    public async Task<List<TicketOrder>> AccessCodesAsync(int streamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Orders.AsNoTracking()
            .Where(o => o.StreamId == streamId && o.Provider == PaymentProvider.Manual)
            .OrderByDescending(o => o.CreatedAt).Take(500).ToListAsync(ct);
    }

    /// <summary>Creates free access codes; each one gives access for its period from the moment it is first used.</summary>
    public async Task<List<TicketOrder>> CreateAccessCodesAsync(ClaimsPrincipal user, int streamId, AccessCodeInput input, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await ManagedStreamAsync(db, user, streamId, ct) is null)
            return [];
        var label = string.IsNullOrWhiteSpace(input.Label) ? "Access code" : input.Label.Trim();
        var redeemBefore = input.RedeemBefore is { } d ? DateTime.SpecifyKind(d.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime() : (DateTime?)null;
        var codes = Enumerable.Range(0, Math.Clamp(input.Count, 1, 500)).Select(_ => new TicketOrder
        {
            StreamId = streamId,
            OfferName = label,
            Currency = "EUR",
            AccessHours = input.AccessHours,
            Provider = PaymentProvider.Manual,
            Status = OrderStatus.Paid,
            AccessToken = CredentialGenerator.Password(32),
            Code = TicketCode.New(),
            RedeemBefore = redeemBefore,
        }).ToList();
        db.Orders.AddRange(codes);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("{Count} access codes created for stream {Stream}", codes.Count, streamId);
        return codes;
    }

    /// <summary>Stops an access code (or a ticket made from one) from working.</summary>
    public async Task<bool> RevokeAccessCodeAsync(ClaimsPrincipal user, int streamId, Guid orderId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await ManagedStreamAsync(db, user, streamId, ct) is null)
            return false;
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.StreamId == streamId && o.Provider == PaymentProvider.Manual, ct);
        if (order is null)
            return false;
        order.Status = OrderStatus.Canceled;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static string? NormalizeEmail(string? email)
    {
        email = email?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(email) && email.Length <= 256 && new EmailAddressAttribute().IsValid(email) && email.Contains('.')
            ? email
            : null;
    }

    private IPaymentGateway Gateway(PaymentProvider provider) =>
        gateways.FirstOrDefault(g => g.Provider == provider) ?? throw new PaymentException("This payment method is not available.");

    private static bool IsPubliclyReachable(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.IsLoopback)
            return false;
        if (!IPAddress.TryParse(uri.Host, out var ip))
            return !uri.Host.EndsWith(".local") && uri.Host.Contains('.');
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127);
    }
}
