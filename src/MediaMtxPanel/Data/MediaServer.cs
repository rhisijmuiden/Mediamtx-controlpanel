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

    /// <summary>Remove path configurations on this server that the panel does not know about.</summary>
    public bool RemoveUnmanagedPaths { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<StreamDefinition> Streams { get; set; } = [];
}
