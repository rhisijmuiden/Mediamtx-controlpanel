using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MediaMtxPanel.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Services.Restream;

public class RestreamTargetInput : IValidatableObject
{
    public RestreamPlatform Platform { get; set; } = RestreamPlatform.YouTube;

    [StringLength(64)]
    public string? Name { get; set; }

    [StringLength(512)]
    public string? ServerUrl { get; set; }

    /// <summary>New stream key. Empty when editing keeps the stored key.</summary>
    [StringLength(512)]
    public string? StreamKey { get; set; }

    public bool Enabled { get; set; } = true;
    public bool TranscodeVideo { get; set; }

    [Range(300, 51000, ErrorMessage = "Choose a video bitrate between 300 and 51000 kbit/s.")]
    public int VideoBitrateKbps { get; set; } = 4500;

    [Range(64, 320, ErrorMessage = "Choose an audio bitrate between 64 and 320 kbit/s.")]
    public int AudioBitrateKbps { get; set; } = 160;

    /// <summary>Set by the form when a key is already stored, so an empty key field is allowed.</summary>
    public bool HasStoredKey { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var url = ServerUrl?.Trim();
        if (Platform == RestreamPlatform.Custom && string.IsNullOrEmpty(url))
            yield return new ValidationResult("Enter the RTMP or RTMPS server URL.", [nameof(ServerUrl)]);
        else if (!string.IsNullOrEmpty(url) && !RestreamService.IsRtmpUrl(url))
            yield return new ValidationResult("The server URL must start with rtmp:// or rtmps://.", [nameof(ServerUrl)]);

        // A custom server may carry the key in its URL already; the platforms always need one.
        if (Platform != RestreamPlatform.Custom && !HasStoredKey && string.IsNullOrWhiteSpace(StreamKey))
            yield return new ValidationResult("Enter the stream key from the platform.", [nameof(StreamKey)]);
        if (StreamKey is { } key && key.Trim().Any(char.IsWhiteSpace))
            yield return new ValidationResult("A stream key cannot contain spaces.", [nameof(StreamKey)]);
    }

    public static RestreamTargetInput From(RestreamTarget t) => new()
    {
        Platform = t.Platform,
        Name = t.Name,
        ServerUrl = t.ServerUrl,
        Enabled = t.Enabled,
        TranscodeVideo = t.TranscodeVideo,
        VideoBitrateKbps = t.VideoBitrateKbps,
        AudioBitrateKbps = t.AudioBitrateKbps,
        HasStoredKey = !string.IsNullOrEmpty(t.StreamKeyProtected),
    };
}

/// <summary>Stores the restream targets of streams. Stream keys are encrypted with Data Protection.</summary>
public class RestreamService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    RestreamManager manager)
{
    public const string ProtectorPurpose = "MediaMtxPanel.RestreamKeys.v1";

    private IDataProtector Protector => dataProtection.CreateProtector(ProtectorPurpose);

    public static bool IsRtmpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "rtmp" or "rtmps" && uri.Host.Length > 0;

    public async Task<List<RestreamTarget>?> ListAsync(ClaimsPrincipal user, int streamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stream = await db.Streams.AsNoTracking().FirstOrDefaultAsync(s => s.Id == streamId, ct);
        if (stream is null || !StreamService.CanManage(user, stream))
            return null;
        return await db.RestreamTargets.AsNoTracking().Where(t => t.StreamId == streamId).OrderBy(t => t.Id).ToListAsync(ct);
    }

    /// <summary>Adds a target (<paramref name="id"/> null) or updates one. Returns null when the user may not manage the stream.</summary>
    public async Task<RestreamTarget?> SaveAsync(ClaimsPrincipal user, int streamId, int? id, RestreamTargetInput input, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stream = await db.Streams.AsNoTracking().FirstOrDefaultAsync(s => s.Id == streamId, ct);
        if (stream is null || !StreamService.CanManage(user, stream))
            return null;

        RestreamTarget? target;
        if (id is null)
        {
            target = new RestreamTarget { StreamId = streamId };
            db.RestreamTargets.Add(target);
        }
        else
        {
            target = await db.RestreamTargets.FirstOrDefaultAsync(t => t.Id == id && t.StreamId == streamId, ct);
            if (target is null)
                return null;
        }

        target.Platform = input.Platform;
        target.Name = string.IsNullOrWhiteSpace(input.Name) ? RestreamPlatforms.Get(input.Platform).Label : input.Name.Trim();
        target.ServerUrl = string.IsNullOrWhiteSpace(input.ServerUrl) ? null : input.ServerUrl.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(input.StreamKey))
            target.StreamKeyProtected = Protector.Protect(input.StreamKey.Trim());
        target.Enabled = input.Enabled;
        target.TranscodeVideo = input.TranscodeVideo;
        target.VideoBitrateKbps = input.VideoBitrateKbps;
        target.AudioBitrateKbps = input.AudioBitrateKbps;
        target.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        manager.Wake();
        return target;
    }

    public async Task<bool> SetEnabledAsync(ClaimsPrincipal user, int targetId, bool enabled, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var target = await db.RestreamTargets.Include(t => t.Stream).FirstOrDefaultAsync(t => t.Id == targetId, ct);
        if (target is null || !StreamService.CanManage(user, target.Stream!))
            return false;
        target.Enabled = enabled;
        target.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        manager.Wake();
        return true;
    }

    public async Task<bool> DeleteAsync(ClaimsPrincipal user, int targetId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var target = await db.RestreamTargets.Include(t => t.Stream).FirstOrDefaultAsync(t => t.Id == targetId, ct);
        if (target is null || !StreamService.CanManage(user, target.Stream!))
            return false;
        db.RestreamTargets.Remove(target);
        await db.SaveChangesAsync(ct);
        manager.Wake();
        return true;
    }
}
