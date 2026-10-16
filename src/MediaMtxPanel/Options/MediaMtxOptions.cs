namespace MediaMtxPanel.Options;

/// <summary>
/// Global MediaMTX settings. When <see cref="ApiUrl"/> is set and the panel has no servers yet,
/// the remaining values are used to create the first server (handy for docker-compose).
/// All other servers are managed in the panel under "Servers".
/// </summary>
public class MediaMtxOptions
{
    public const string Section = "MediaMtx";

    /// <summary>How often the panel re-applies its stream configuration to every server.</summary>
    public int SyncIntervalSeconds { get; set; } = 30;

    // --- First server, created on startup when no servers exist ---

    public string ServerName { get; set; } = "Default";
    public string ApiUrl { get; set; } = "";
    public string ApiUser { get; set; } = "panel";
    public string ApiPassword { get; set; } = "";
    public string AuthHookSecret { get; set; } = "";
    public string PublicHost { get; set; } = "localhost";
    public int RtspPort { get; set; } = 8554;
    public int RtmpPort { get; set; } = 1935;
    public int SrtPort { get; set; } = 8890;
    public int HlsPort { get; set; } = 8888;
    public int WebRtcPort { get; set; } = 8889;
    public bool HttpsUrls { get; set; }
}

public class PanelOptions
{
    public const string Section = "Panel";

    /// <summary>Administrator account created on first start when no administrator exists.</summary>
    public string AdminEmail { get; set; } = "admin@localhost";
    public string AdminPassword { get; set; } = "";

    /// <summary>Treat the first administrator password as one-time: it must be changed at the first login.</summary>
    public bool AdminMustChangePassword { get; set; } = true;

    /// <summary>
    /// Address at which MediaMTX servers reach the panel's auth hook, e.g. http://10.0.0.10:8080.
    /// Only used to show ready-to-paste configuration; empty means "use the address of the current request".
    /// </summary>
    public string? HookBaseUrl { get; set; }

    /// <summary>
    /// Public address of the panel, e.g. https://stream.example.com. Used for payment return and
    /// webhook URLs and for watch links. Empty means "use the address of the current request".
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>Offer a simulated payment method, to try the paywall without a Mollie or PayPal account.</summary>
    public bool EnableTestPayments { get; set; }
}
