using System.ComponentModel.DataAnnotations;

namespace MediaMtxPanel.Data;

/// <summary>A way to buy access to a paywalled stream, e.g. "Day pass €4.95, 24 hours".</summary>
public class StreamOffer
{
    public int Id { get; set; }

    public int StreamId { get; set; }
    public StreamDefinition? Stream { get; set; }

    [MaxLength(80)]
    public string Name { get; set; } = "";

    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>Price in cents.</summary>
    public int PriceCents { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    /// <summary>How long a ticket gives access after payment.</summary>
    public int AccessHours { get; set; } = 24;

    public bool Active { get; set; } = true;

    public int SortOrder { get; set; }
}

public enum PaymentProvider
{
    Mollie = 0,
    PayPal = 1,
    /// <summary>An access code the stream owner handed out (free); its access period starts at first use.</summary>
    Manual = 8,
    /// <summary>Simulated payments for trying out the flow; only when Panel:EnableTestPayments is on.</summary>
    Test = 9,
}

public enum OrderStatus
{
    Open = 0,
    Paid = 1,
    Failed = 2,
    Canceled = 3,
    Expired = 4,
}

/// <summary>A purchase of an offer. When paid it carries the viewer's access ticket.</summary>
public class TicketOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int StreamId { get; set; }
    public StreamDefinition? Stream { get; set; }

    public int? OfferId { get; set; }
    public StreamOffer? Offer { get; set; }

    /// <summary>Copied from the offer at purchase time, so later price changes do not alter history.</summary>
    [MaxLength(80)]
    public string OfferName { get; set; } = "";
    public int AmountCents { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";
    public int AccessHours { get; set; }

    [MaxLength(256)]
    public string? BuyerEmail { get; set; }

    public PaymentProvider Provider { get; set; }

    [MaxLength(128)]
    public string? ProviderPaymentId { get; set; }

    public OrderStatus Status { get; set; }

    /// <summary>Secret the viewer uses as password (user "ticket") to watch the stream.</summary>
    [MaxLength(64)]
    public string AccessToken { get; set; } = "";

    /// <summary>
    /// Short ticket code the viewer can type in (on the watch page, in the embedded player or under My tickets),
    /// stored without dashes, see <see cref="TicketCode"/>.
    /// </summary>
    [MaxLength(16)]
    public string? Code { get; set; }

    /// <summary>Access codes (<see cref="PaymentProvider.Manual"/>) must be used before this moment; null means no deadline.</summary>
    public DateTime? RedeemBefore { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }
    public DateTime? AccessExpiresAt { get; set; }

    public bool GivesAccess(DateTime now) => Status == OrderStatus.Paid && AccessExpiresAt > now;

    /// <summary>An access code nobody has used yet.</summary>
    public bool IsUnusedCode => Provider == PaymentProvider.Manual && Status == OrderStatus.Paid && AccessExpiresAt is null;
}

/// <summary>Ticket codes like K7QM-4XPA-9TRC: 12 characters without look-alikes (about 59 bits).</summary>
public static class TicketCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 12;

    public static string New() => System.Security.Cryptography.RandomNumberGenerator.GetString(Alphabet, Length);

    /// <summary>Uppercase without dashes or spaces; also accepts a pasted link with ?code=.</summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";
        var text = input.Trim();
        var at = text.IndexOf("code=", StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
            text = text[(at + 5)..].Split('&')[0];
        return new string(text.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    public static string Display(string? code) =>
        string.IsNullOrEmpty(code) || code.Length != Length ? code ?? "" : $"{code[..4]}-{code[4..8]}-{code[8..]}";
}

/// <summary>
/// Payment provider credentials of a customer (or other stream owner). Money from ticket sales of
/// their streams goes to these accounts. Secrets are encrypted with ASP.NET Data Protection.
/// </summary>
public class PaymentAccount
{
    public int Id { get; set; }

    [MaxLength(450)]
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public bool MollieEnabled { get; set; }
    [MaxLength(1024)]
    public string? MollieApiKeyProtected { get; set; }

    public bool PayPalEnabled { get; set; }
    [MaxLength(256)]
    public string? PayPalClientId { get; set; }
    [MaxLength(1024)]
    public string? PayPalSecretProtected { get; set; }
    public bool PayPalSandbox { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
