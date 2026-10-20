using System.Security.Claims;
using MediaMtxPanel.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Services;

public enum EmbedImage { Logo, Poster }

/// <summary>Settings of the embeddable player per stream (see <see cref="StreamEmbed"/>).</summary>
public class EmbedService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public const int MaxImageBytes = 1024 * 1024;

    // No SVG: the images are served from the panel's own origin, and SVG can carry scripts.
    public static readonly IReadOnlyDictionary<string, string> ImageTypes = new Dictionary<string, string>
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
    };

    /// <summary>The stored settings, or the defaults for a stream that has none yet.</summary>
    public async Task<StreamEmbed> GetAsync(int streamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Embeds.AsNoTracking().FirstOrDefaultAsync(e => e.StreamId == streamId, ct)
            ?? new StreamEmbed { StreamId = streamId, UpdatedAt = DateTime.MinValue };
    }

    public async Task<bool> SaveAsync(ClaimsPrincipal user, int streamId, EmbedLook look, string? allowedSites, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var embed = await EditableAsync(db, user, streamId, ct);
        if (embed is null)
            return false;
        embed.Look = look;
        var sites = StreamEmbed.ParseSites(allowedSites);
        embed.AllowedSites = sites.Count == 0 ? null : string.Join('\n', sites);
        embed.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Stores or (with null data) removes the logo or poster image.</summary>
    public async Task<bool> SetImageAsync(ClaimsPrincipal user, int streamId, EmbedImage kind, byte[]? data, string? contentType, CancellationToken ct = default)
    {
        if (data is not null && (data.Length > MaxImageBytes || contentType is null || !ImageTypes.ContainsKey(contentType)))
            return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var embed = await EditableAsync(db, user, streamId, ct);
        if (embed is null)
            return false;
        if (kind == EmbedImage.Logo)
            (embed.LogoData, embed.LogoContentType) = (data, data is null ? null : contentType);
        else
            (embed.PosterData, embed.PosterContentType) = (data, data is null ? null : contentType);
        embed.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static async Task<StreamEmbed?> EditableAsync(ApplicationDbContext db, ClaimsPrincipal user, int streamId, CancellationToken ct)
    {
        var stream = await db.Streams.AsNoTracking().FirstOrDefaultAsync(s => s.Id == streamId, ct);
        if (stream is null || !StreamService.CanManage(user, stream))
            return null;
        var embed = await db.Embeds.FirstOrDefaultAsync(e => e.StreamId == streamId, ct);
        if (embed is null)
        {
            embed = new StreamEmbed { StreamId = streamId };
            db.Embeds.Add(embed);
        }
        return embed;
    }
}
