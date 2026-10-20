using System.ComponentModel.DataAnnotations;

namespace MediaMtxPanel.Data;

/// <summary>A MediaMTX instance that the panel manages through its Control API.</summary>
public class MediaServer
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Name { get; set; } = "";

    [MaxLength(512)]
    public string? Description { get; set; }

    /// <summary>Base URL of the Control API as the panel reaches it, e.g. http://mediamtx:9997.</summary>
    [MaxLength(256)]
    public string ApiUrl { get; set; } = "";

    /// <summary>Credentials the panel uses for the Control API; MediaMTX checks them through the auth hook.</summary>
    [MaxLength(64)]
    public string ApiUser { get; set; } = "panel";

    [MaxLength(128)]
    public string ApiPassword { get; set; } = "";

    /// <summary>Secret MediaMTX sends to the auth hook as ?secret=...; identifies this server.</summary>
    [MaxLength(128)]
    public string HookSecret { get; set; } = "";

    /// <summary>Host name or IP that publishers and viewers use to reach this server.</summary>
    [MaxLength(256)]
    public string PublicHost { get; set; } = "localhost";

    public int RtspPort { get; set; } = 8554;
    public int RtmpPort { get; set; } = 1935;
    public int SrtPort { get; set; } = 8890;
    public int HlsPort { get; set; } = 8888;
    public int WebRtcPort { get; set; } = 8889;

    /// <summary>Use https:// for HLS and WebRTC URLs.</summary>
    public bool HttpsUrls { get; set; }

    /// <summary>How browsers and players reach HLS and WebRTC on this server; see <see cref="Data.ViewerRoute"/>.</summary>
    public ViewerRoute ViewerRoute { get; set; }

    /// <summary>Public base URL of HLS for <see cref="ViewerRoute.Custom"/>, e.g. https://stream.example.com/hls.</summary>
    [MaxLength(256)]
    public string? PublicHlsUrl { get; set; }

    /// <summary>Public base URL of WebRTC for <see cref="ViewerRoute.Custom"/>, e.g. https://stream.example.com/webrtc.</summary>
    [MaxLength(256)]
    public string? PublicWebRtcUrl { get; set; }

    /// <summary>Remove path configurations on this server that the panel does not know about.</summary>
    public bool RemoveUnmanagedPaths { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional Prometheus node_exporter URL (e.g. http://10.0.0.21:9100/metrics) for CPU/RAM/network of
    /// the server. Empty with a loopback API URL means "same machine as the panel": read /proc directly.
    /// </summary>
    [MaxLength(256)]
    public string? MetricsUrl { get; set; }

    /// <summary>
    /// Base URL of the MediaMTX playback server (recordings) as the panel reaches it, e.g. http://10.0.0.21:9996.
    /// Empty: the host of <see cref="ApiUrl"/> on port <see cref="DefaultPlaybackPort"/>.
    /// </summary>
    [MaxLength(256)]
    public string? PlaybackUrl { get; set; }

    public const int DefaultPlaybackPort = 9996;

    public string PlaybackBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(PlaybackUrl))
            return PlaybackUrl.Trim().TrimEnd('/');
        return Uri.TryCreate(ApiUrl, UriKind.Absolute, out var api)
            ? $"{api.Scheme}://{new UriBuilder(api) { Port = DefaultPlaybackPort }.Uri.Authority}"
            : $"http://127.0.0.1:{DefaultPlaybackPort}";
    }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<StreamDefinition> Streams { get; set; } = [];
}

/// <summary>How viewers reach HLS and WebRTC (including WHIP/WHEP) on a server. RTSP, RTMP and SRT always use the ports.</summary>
public enum ViewerRoute
{
    /// <summary><see cref="Panel"/> when Settings → Web server says the panel is behind a reverse proxy, otherwise <see cref="Direct"/>.</summary>
    Auto,
    /// <summary>Straight to the MediaMTX ports: http(s)://host:8888 and :8889.</summary>
    Direct,
    /// <summary>Through the panel at /media/{server}/hls and /media/{server}/webrtc, so on the panel's own https address.</summary>
    Panel,
    /// <summary>Public URLs the administrator set up in a reverse proxy.</summary>
    Custom,
}
