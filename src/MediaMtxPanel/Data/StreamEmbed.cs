using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaMtxPanel.Data;

/// <summary>
/// How the embeddable player (/embed/{slug}) of a stream looks and behaves. The look is stored as
/// JSON, so new options need no migration; images are stored in the database and so are part of backups.
/// </summary>
public partial class StreamEmbed
{
    [Key]
    public int StreamId { get; set; }
    public StreamDefinition? Stream { get; set; }

    public string LookJson { get; set; } = "{}";

    public byte[]? LogoData { get; set; }
    [MaxLength(64)]
    public string? LogoContentType { get; set; }

    public byte[]? PosterData { get; set; }
    [MaxLength(64)]
    public string? PosterContentType { get; set; }

    /// <summary>Websites that may embed the player, one per line; empty allows every website.</summary>
    [MaxLength(2048)]
    public string? AllowedSites { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public EmbedLook Look
    {
        get
        {
            try
            {
                return (JsonSerializer.Deserialize<EmbedLook>(LookJson, Json) ?? new EmbedLook()).Normalized();
            }
            catch (JsonException)
            {
                return new EmbedLook();
            }
        }
        set => LookJson = JsonSerializer.Serialize(value.Normalized(), Json);
    }

    /// <summary>Hosts from <see cref="AllowedSites"/> as CSP frame-ancestors sources.</summary>
    public static IReadOnlyList<string> ParseSites(string? text) =>
        (text ?? "").Split(['\n', '\r', ',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Regex.Replace(s.ToLowerInvariant(), "^https?://", "").TrimEnd('/'))
            .Where(s => SiteRegex().IsMatch(s))
            .Distinct()
            .ToList();

    [GeneratedRegex(@"^(\*\.)?[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*(:\d{1,5})?$")]
    private static partial Regex SiteRegex();
}

/// <summary>The cosmetic and playback options of the embedded player. Also sent to wwwroot/embed/embed.js as JSON.</summary>
public record EmbedLook
{
    public static readonly string[] AspectRatios = ["16:9", "4:3", "21:9", "1:1", "9:16"];
    public static readonly string[] LogoPositions = ["top-left", "top-right", "bottom-left", "bottom-right"];

    public string Accent { get; set; } = "#6366f1";
    public string Background { get; set; } = "#0b0f19";
    public int Radius { get; set; } = 12;
    public string Aspect { get; set; } = "16:9";
    /// <summary>Fill the width of the page (responsive) or use a fixed <see cref="Width"/>.</summary>
    public bool Responsive { get; set; } = true;
    public int Width { get; set; } = 640;

    public bool Autoplay { get; set; } = true;
    public bool Muted { get; set; } = true;
    public bool Controls { get; set; } = true;
    public bool ShowTitle { get; set; } = true;
    public bool ShowLive { get; set; } = true;
    public bool ShowPip { get; set; } = true;
    public bool ShowFullscreen { get; set; } = true;
    /// <summary>"webrtc" (lowest latency) or "hls" (most compatible).</summary>
    public string Protocol { get; set; } = "webrtc";

    public string LogoPosition { get; set; } = "top-right";
    /// <summary>Logo width as a percentage of the player width.</summary>
    public int LogoSize { get; set; } = 14;
    public int LogoOpacity { get; set; } = 90;
    public string? LogoLink { get; set; }

    /// <summary>Shown while the stream is not live; empty uses the default text.</summary>
    public string? OfflineText { get; set; }

    public EmbedLook Normalized() => this with
    {
        Accent = Color(Accent, "#6366f1"),
        Background = Color(Background, "#0b0f19"),
        Radius = Math.Clamp(Radius, 0, 32),
        Aspect = AspectRatios.Contains(Aspect) ? Aspect : "16:9",
        Width = Math.Clamp(Width, 240, 1920),
        Protocol = Protocol == "hls" ? "hls" : "webrtc",
        LogoPosition = LogoPositions.Contains(LogoPosition) ? LogoPosition : "top-right",
        LogoSize = Math.Clamp(LogoSize, 5, 40),
        LogoOpacity = Math.Clamp(LogoOpacity, 10, 100),
        LogoLink = Uri.TryCreate(LogoLink?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? u.ToString() : null,
        OfflineText = string.IsNullOrWhiteSpace(OfflineText) ? null : OfflineText.Trim()[..Math.Min(OfflineText.Trim().Length, 200)],
    };

    /// <summary>Height for a fixed-size embed of <see cref="Width"/> pixels.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Height
    {
        get
        {
            var parts = Aspect.Split(':');
            return (int)Math.Round(Width * double.Parse(parts[1]) / double.Parse(parts[0]));
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string CssAspect => Aspect.Replace(":", " / ");

    private static string Color(string? value, string fallback) =>
        value is not null && Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value.ToLowerInvariant() : fallback;
}
