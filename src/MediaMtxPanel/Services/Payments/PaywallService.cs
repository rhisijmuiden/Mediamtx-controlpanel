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
    [Required(ErrorMessage = "Geef de aanbieding een naam."), StringLength(80)]
    public string Name { get; set; } = "";

    [StringLength(300)]
    public string? Description { get; set; }

    [Range(0.5, 10000, ErrorMessage = "Kies een prijs tussen € 0,50 en € 10.000.")]
    public decimal Price { get; set; } = 4.95m;

    [Range(1, 24 * 366, ErrorMessage = "Kies een toegangsduur van minimaal 1 uur.")]
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
            throw new PaymentException("De betaalgegevens van de verkoper kunnen niet worden gelezen. Vraag de verkoper ze opnieuw in te voeren.");
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
            return "Stream niet gevonden.";
        if (enabled)
        {
            if (!await db.Offers.AnyAsync(o => o.StreamId == streamId && o.Active, ct))
                return "Voeg eerst minstens één actieve aanbieding toe.";
            if ((await ProvidersForOwnerAsync(s.OwnerId, ct)).Count == 0)
                return "Stel eerst een betaalaccount in (iDEAL via Mollie of PayPal).";
        }
        s.PaywallEnabled = enabled;
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<SalesSummary> SalesAsync(int streamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var paid = db.Orders.AsNoTracking().Where(o => o.StreamId == streamId && o.Status == OrderStatus.Paid);
        var now = DateTime.UtcNow;
        return new SalesSummary(
            await paid.CountAsync(ct),
            await paid.SumAsync(o => (int?)o.AmountCents, ct) ?? 0,
            await paid.CountAsync(o => o.AccessExpiresAt > now, ct),
            await db.Orders.AsNoTracking().Where(o => o.StreamId == streamId && o.Status != OrderStatus.Open)
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
            ?? throw new PaymentException("Stream niet gevonden.");
        if (!stream.PaywallEnabled || !stream.Enabled)
            throw new PaymentException("Voor deze stream worden geen tickets verkocht.");
        var offer = await db.Offers.AsNoTracking().FirstOrDefaultAsync(o => o.Id == offerId && o.StreamId == stream.Id && o.Active, ct)
            ?? throw new PaymentException("Deze aanbieding bestaat niet meer.");
        if (!(await ProvidersForOwnerAsync(stream.OwnerId, ct)).Contains(provider))
            throw new PaymentException("Deze betaalmethode is niet beschikbaar.");

        var order = new TicketOrder
        {
            StreamId = stream.Id,
            OfferId = offer.Id,
            OfferName = offer.Name,
            AmountCents = offer.PriceCents,
            Currency = offer.Currency,
            AccessHours = offer.AccessHours,
            BuyerEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
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
            throw ex as PaymentException ?? new PaymentException("De betaalprovider is niet bereikbaar. Probeer het later opnieuw.");
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

    private IPaymentGateway Gateway(PaymentProvider provider) =>
        gateways.FirstOrDefault(g => g.Provider == provider) ?? throw new PaymentException("Deze betaalmethode is niet beschikbaar.");

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
