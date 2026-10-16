using System.ComponentModel.DataAnnotations;

namespace MediaMtxPanel.Data;

/// <summary>A way to buy access to a paywalled stream, e.g. "Dagticket € 4,95, 24 uur".</summary>
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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }
    public DateTime? AccessExpiresAt { get; set; }

    public bool GivesAccess(DateTime now) => Status == OrderStatus.Paid && AccessExpiresAt > now;
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
