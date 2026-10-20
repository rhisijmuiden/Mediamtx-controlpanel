using System.ComponentModel.DataAnnotations;

namespace MediaMtxPanel.Data;

public enum StreamSourceType
{
    /// <summary>Clients push the stream into MediaMTX (OBS, ffmpeg, cameras, WHIP...).</summary>
    Publisher = 0,

    /// <summary>MediaMTX pulls the stream from an external URL (rtsp://, rtmp://, srt://, http(s)://...m3u8, whep://).</summary>
    Pull = 1,
}

public class StreamDefinition
{
    public int Id { get; set; }

    public int ServerId { get; set; }
    public MediaServer? Server { get; set; }

    /// <summary>MediaMTX path name, e.g. "studio1" or "events/main".</summary>
    [MaxLength(64)]
    public string Name { get; set; } = "";

    [MaxLength(128)]
    public string DisplayName { get; set; } = "";

    [MaxLength(1024)]
    public string? Description { get; set; }

    public StreamSourceType SourceType { get; set; }

    [MaxLength(1024)]
    public string? SourceUrl { get; set; }

    public bool SourceOnDemand { get; set; }

    public bool Enabled { get; set; } = true;

    public bool Record { get; set; }

    /// <summary>MediaMTX deletes recordings older than this many days; 0 keeps them until they are deleted by hand.</summary>
    public int RecordRetentionDays { get; set; } = DefaultRetentionDays;

    public const int DefaultRetentionDays = 7;

    /// <summary>Allow viewers without credentials.</summary>
    public bool PublicRead { get; set; }

    /// <summary>0 means unlimited.</summary>
    public int MaxReaders { get; set; }

    [MaxLength(64)]
    public string PublishUser { get; set; } = "";

    [MaxLength(128)]
    public string PublishPassword { get; set; } = "";

    [MaxLength(64)]
    public string ReadUser { get; set; } = "";

    [MaxLength(128)]
    public string ReadPassword { get; set; } = "";

    /// <summary>Viewers must buy a ticket (see <see cref="StreamOffer"/>) to watch.</summary>
    public bool PaywallEnabled { get; set; }

    /// <summary>Random, unguessable part of the public watch page URL (/watch/{slug}).</summary>
    [MaxLength(32)]
    public string WatchSlug { get; set; } = "";

    public List<StreamOffer> Offers { get; set; } = [];

    public List<RestreamTarget> RestreamTargets { get; set; } = [];

    public string? OwnerId { get; set; }
    public ApplicationUser? Owner { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
