using System.ComponentModel.DataAnnotations;

namespace MediaMtxPanel.Data;

public enum RestreamPlatform
{
    YouTube = 0,
    Twitch = 1,
    Facebook = 2,
    /// <summary>Any other RTMP(S) server: Kick, LinkedIn, a CDN, another MediaMTX...</summary>
    Custom = 3,
}

/// <summary>
/// Forwards a stream to an external platform. While the stream is live and the target is enabled,
/// the panel runs ffmpeg: it reads the stream from its MediaMTX server and pushes it to the target.
/// </summary>
public class RestreamTarget
{
    public int Id { get; set; }

    public int StreamId { get; set; }
    public StreamDefinition? Stream { get; set; }

    public RestreamPlatform Platform { get; set; }

    [MaxLength(64)]
    public string Name { get; set; } = "";

    /// <summary>RTMP(S) server URL; empty means the platform's default ingest server.</summary>
    [MaxLength(512)]
    public string? ServerUrl { get; set; }

    /// <summary>Stream key, encrypted with ASP.NET Data Protection; never stored or shown in plain text.</summary>
    [MaxLength(2048)]
    public string? StreamKeyProtected { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Re-encode the video to H.264 instead of passing it through (for H.265/VP8/AV1 sources or a lower bitrate).</summary>
    public bool TranscodeVideo { get; set; }

    public int VideoBitrateKbps { get; set; } = 4500;

    public int AudioBitrateKbps { get; set; } = 160;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public record RestreamPlatformInfo(RestreamPlatform Platform, string Label, string Icon, string? DefaultUrl, string? KeyHelpUrl);

public static class RestreamPlatforms
{
    public static readonly IReadOnlyList<RestreamPlatformInfo> All =
    [
        new(RestreamPlatform.YouTube, "YouTube", "bi-youtube", "rtmps://a.rtmps.youtube.com:443/live2", "https://studio.youtube.com/"),
        new(RestreamPlatform.Twitch, "Twitch", "bi-twitch", "rtmps://live.twitch.tv:443/app", "https://dashboard.twitch.tv/settings/stream"),
        new(RestreamPlatform.Facebook, "Facebook", "bi-facebook", "rtmps://live-api-s.facebook.com:443/rtmp", "https://www.facebook.com/live/producer"),
        new(RestreamPlatform.Custom, "Custom RTMP(S)", "bi-broadcast-pin", null, null),
    ];

    public static RestreamPlatformInfo Get(RestreamPlatform p) => All.First(i => i.Platform == p);
}
