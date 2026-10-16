using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.RegularExpressions;
using MediaMtxPanel.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Services;

public partial class StreamInput : IValidatableObject
{
    [Required(ErrorMessage = "Geef de stream een padnaam.")]
    [StringLength(64)]
    [RegularExpression(@"^[a-zA-Z0-9][a-zA-Z0-9_\-./]*$",
        ErrorMessage = "Gebruik alleen letters, cijfers, '-', '_', '.' en '/', en begin met een letter of cijfer.")]
    public string Name { get; set; } = "";

    [StringLength(128)]
    public string? DisplayName { get; set; }

    [StringLength(1024)]
    public string? Description { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Kies een server.")]
    public int ServerId { get; set; }

    public StreamSourceType SourceType { get; set; } = StreamSourceType.Publisher;

    [StringLength(1024)]
    public string? SourceUrl { get; set; }

    public bool SourceOnDemand { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Record { get; set; }
    public bool PublicRead { get; set; }

    [Range(0, 10000)]
    public int MaxReaders { get; set; }

    public string? OwnerId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Name.Contains("..") || Name.EndsWith('/') || Name.Contains("//"))
            yield return new ValidationResult("Ongeldige padnaam.", [nameof(Name)]);

        if (SourceType == StreamSourceType.Pull)
        {
            if (string.IsNullOrWhiteSpace(SourceUrl) || !SourceUrlPattern().IsMatch(SourceUrl))
                yield return new ValidationResult(
                    "Geef een bron-URL op (rtsp://, rtsps://, rtmp://, rtmps://, srt://, http(s)://...m3u8, whep(s)://, udp://).",
                    [nameof(SourceUrl)]);
        }
    }

    public static StreamInput From(StreamDefinition s) => new()
    {
        Name = s.Name,
        ServerId = s.ServerId,
        DisplayName = s.DisplayName,
        Description = s.Description,
        SourceType = s.SourceType,
        SourceUrl = s.SourceUrl,
        SourceOnDemand = s.SourceOnDemand,
        Enabled = s.Enabled,
        Record = s.Record,
        PublicRead = s.PublicRead,
        MaxReaders = s.MaxReaders,
        OwnerId = s.OwnerId,
    };

    [GeneratedRegex(@"^(rtsps?|rtmps?|srt|https?|wheps?|udp|udp\+mpegts|unix\+mpegts|udp\+rtp|moq)://\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceUrlPattern();
}

public enum CredentialKind { Publish, Read }

/// <summary>
/// Stores streams in the panel database and keeps the path configuration of the MediaMTX
/// server each stream runs on in step. The database is the source of truth; the servers only hold a copy.
/// </summary>
public class StreamService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    MediaMtxClientFactory clients,
    ILogger<StreamService> logger)
{
    public static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole(Roles.Admin);

    public static string? UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static bool CanManage(ClaimsPrincipal user, StreamDefinition s) =>
        IsAdmin(user) || (s.OwnerId is not null && s.OwnerId == UserId(user));

    public async Task<List<StreamDefinition>> ListAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Streams.Include(s => s.Owner).Include(s => s.Server).AsNoTracking();
        if (!IsAdmin(user))
        {
            var id = UserId(user);
            q = q.Where(s => s.OwnerId == id);
        }
        return await q.OrderBy(s => s.Server!.Name).ThenBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<StreamDefinition?> GetAsync(ClaimsPrincipal user, int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Streams.Include(x => x.Owner).Include(x => x.Server).AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        return s is not null && CanManage(user, s) ? s : null;
    }

    public async Task<bool> NameExistsAsync(int serverId, string name, int? exceptId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Streams.AnyAsync(s => s.ServerId == serverId && s.Name == name && s.Id != exceptId, ct);
    }

    public async Task<(StreamDefinition Stream, string? Warning)> CreateAsync(
        ClaimsPrincipal user, StreamInput input, CancellationToken ct = default)
    {
        var stream = new StreamDefinition
        {
            Name = input.Name.Trim(),
            ServerId = input.ServerId,
            PublishUser = CredentialGenerator.UserName("pub"),
            PublishPassword = CredentialGenerator.Password(),
            ReadUser = CredentialGenerator.UserName("view"),
            ReadPassword = CredentialGenerator.Password(),
            OwnerId = IsAdmin(user) && !string.IsNullOrEmpty(input.OwnerId) ? input.OwnerId : UserId(user),
        };
        Apply(stream, input);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Streams.Add(stream);
        await db.SaveChangesAsync(ct);
        await db.Entry(stream).Reference(s => s.Server).LoadAsync(ct);

        logger.LogInformation("Stream {Name} created on server {Server} by {User}",
            stream.Name, stream.Server!.Name, user.Identity?.Name);
        return (stream, await PushAsync(stream, previous: null, ct));
    }

    public async Task<(StreamDefinition? Stream, string? Warning)> UpdateAsync(
        ClaimsPrincipal user, int id, StreamInput input, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stream = await db.Streams.Include(s => s.Server).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stream is null || !CanManage(user, stream))
            return (null, null);

        var previous = (stream.Server!, stream.Name);
        stream.Name = input.Name.Trim();
        stream.ServerId = input.ServerId;
        Apply(stream, input);
        if (IsAdmin(user))
            stream.OwnerId = string.IsNullOrEmpty(input.OwnerId) ? null : input.OwnerId;
        await db.SaveChangesAsync(ct);
        if (stream.Server?.Id != stream.ServerId)
        {
            stream.Server = null;
            await db.Entry(stream).Reference(s => s.Server).LoadAsync(ct);
        }

        return (stream, await PushAsync(stream, previous, ct));
    }

    public async Task<(StreamDefinition? Stream, string? Warning)> SetEnabledAsync(
        ClaimsPrincipal user, int id, bool enabled, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stream = await db.Streams.Include(s => s.Server).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stream is null || !CanManage(user, stream))
            return (null, null);

        stream.Enabled = enabled;
        stream.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return (stream, await PushAsync(stream, previous: null, ct));
    }

    public async Task<StreamDefinition?> RegenerateAsync(
        ClaimsPrincipal user, int id, CredentialKind kind, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stream = await db.Streams.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stream is null || !CanManage(user, stream))
            return null;

        if (kind == CredentialKind.Publish)
        {
            stream.PublishUser = CredentialGenerator.UserName("pub");
            stream.PublishPassword = CredentialGenerator.Password();
        }
        else
        {
            stream.ReadUser = CredentialGenerator.UserName("view");
            stream.ReadPassword = CredentialGenerator.Password();
        }
        stream.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // Credentials are checked by the auth hook on every new connection, so MediaMTX needs no update.
        return stream;
    }

    public async Task<(bool Deleted, string? Warning)> DeleteAsync(ClaimsPrincipal user, int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stream = await db.Streams.Include(s => s.Server).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (stream is null || !CanManage(user, stream))
            return (false, null);

        db.Streams.Remove(stream);
        await db.SaveChangesAsync(ct);

        try
        {
            await clients.Create(stream.Server!).DeletePathConfigAsync(stream.Name, ct);
            return (true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or MediaMtxException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not remove path {Name} from {Server}", stream.Name, stream.Server!.Name);
            return (true, $"De stream is verwijderd, maar server {stream.Server!.Name} kon niet worden bijgewerkt: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the stream's configuration to its server, and removes the old path when the stream was
    /// renamed or moved to another server. Returns a warning when a server could not be updated.
    /// </summary>
    public async Task<string?> PushAsync(StreamDefinition stream, (MediaServer Server, string Name)? previous, CancellationToken ct = default)
    {
        var server = stream.Server!;
        try
        {
            if (previous is { } prev && (prev.Server.Id != server.Id || prev.Name != stream.Name))
                await clients.Create(prev.Server).DeletePathConfigAsync(prev.Name, ct);

            var mtx = clients.Create(server);
            if (!stream.Enabled || !server.Enabled)
            {
                await mtx.DeletePathConfigAsync(stream.Name, ct);
                return null;
            }

            var desired = ToConfig(stream);
            var current = await mtx.GetPathConfigAsync(stream.Name, ct);
            if (current is null)
                await mtx.AddPathConfigAsync(stream.Name, desired, ct);
            else if (!Matches(current, desired))
                await mtx.ReplacePathConfigAsync(stream.Name, desired, ct);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or MediaMtxException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not apply stream {Name} to {Server}", stream.Name, server.Name);
            return $"Opgeslagen, maar server {server.Name} kon niet worden bijgewerkt: {ex.Message}. " +
                   "Het paneel probeert het automatisch opnieuw.";
        }
    }
    public static MtxPathConfig ToConfig(StreamDefinition s) => new()
    {
        Source = s.SourceType == StreamSourceType.Pull ? s.SourceUrl : "publisher",
        SourceOnDemand = s.SourceType == StreamSourceType.Pull && s.SourceOnDemand,
        Record = s.Record,
        MaxReaders = s.MaxReaders,
    };

    public static bool Matches(MtxPathConfig current, MtxPathConfig desired) =>
        current.Source == desired.Source
        && (current.SourceOnDemand ?? false) == (desired.SourceOnDemand ?? false)
        && (current.Record ?? false) == (desired.Record ?? false)
        && (current.MaxReaders ?? 0) == (desired.MaxReaders ?? 0);

    private static void Apply(StreamDefinition s, StreamInput input)
    {
        s.DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? s.Name : input.DisplayName.Trim();
        s.Description = input.Description?.Trim();
        s.SourceType = input.SourceType;
        s.SourceUrl = input.SourceType == StreamSourceType.Pull ? input.SourceUrl?.Trim() : null;
        s.SourceOnDemand = input.SourceOnDemand;
        s.Enabled = input.Enabled;
        s.Record = input.Record;
        s.PublicRead = input.PublicRead;
        s.MaxReaders = input.MaxReaders;
        s.UpdatedAt = DateTime.UtcNow;
    }
}
