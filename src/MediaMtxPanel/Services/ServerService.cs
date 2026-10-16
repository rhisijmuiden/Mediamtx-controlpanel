using System.ComponentModel.DataAnnotations;
using MediaMtxPanel.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Services;

public class ServerInput
{
    [Required(ErrorMessage = "Geef de server een naam."), StringLength(64)]
    public string Name { get; set; } = "";

    [StringLength(512)]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Vul de API-URL in.")]
    [RegularExpression(@"^https?://\S+$", ErrorMessage = "Gebruik een URL als http://10.0.0.5:9997.")]
    public string ApiUrl { get; set; } = "http://";

    [Required, StringLength(64)]
    public string ApiUser { get; set; } = "panel";

    [Required(ErrorMessage = "Vul een API-wachtwoord in."), StringLength(128)]
    public string ApiPassword { get; set; } = CredentialGenerator.Password(24);

    [Required(ErrorMessage = "Vul het publieke adres in."), StringLength(256)]
    public string PublicHost { get; set; } = "";

    [Range(1, 65535)] public int RtspPort { get; set; } = 8554;
    [Range(1, 65535)] public int RtmpPort { get; set; } = 1935;
    [Range(1, 65535)] public int SrtPort { get; set; } = 8890;
    [Range(1, 65535)] public int HlsPort { get; set; } = 8888;
    [Range(1, 65535)] public int WebRtcPort { get; set; } = 8889;

    public bool HttpsUrls { get; set; }
    public bool RemoveUnmanagedPaths { get; set; }
    public bool Enabled { get; set; } = true;

    public static ServerInput From(MediaServer s) => new()
    {
        Name = s.Name,
        Description = s.Description,
        ApiUrl = s.ApiUrl,
        ApiUser = s.ApiUser,
        ApiPassword = s.ApiPassword,
        PublicHost = s.PublicHost,
        RtspPort = s.RtspPort,
        RtmpPort = s.RtmpPort,
        SrtPort = s.SrtPort,
        HlsPort = s.HlsPort,
        WebRtcPort = s.WebRtcPort,
        HttpsUrls = s.HttpsUrls,
        RemoveUnmanagedPaths = s.RemoveUnmanagedPaths,
        Enabled = s.Enabled,
    };

    public void ApplyTo(MediaServer s)
    {
        s.Name = Name.Trim();
        s.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        s.ApiUrl = ApiUrl.Trim().TrimEnd('/');
        s.ApiUser = ApiUser.Trim();
        s.ApiPassword = ApiPassword;
        s.PublicHost = PublicHost.Trim();
        s.RtspPort = RtspPort;
        s.RtmpPort = RtmpPort;
        s.SrtPort = SrtPort;
        s.HlsPort = HlsPort;
        s.WebRtcPort = WebRtcPort;
        s.HttpsUrls = HttpsUrls;
        s.RemoveUnmanagedPaths = RemoveUnmanagedPaths;
        s.Enabled = Enabled;
    }
}

public record ServerTestResult(bool Ok, string Message);

/// <summary>Management of the MediaMTX servers the panel controls.</summary>
public class ServerService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    MediaMtxClientFactory clients,
    MediaMtxSyncService sync,
    MediaMtxStatus status)
{
    public async Task<List<MediaServer>> ListAsync(bool enabledOnly = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Servers.AsNoTracking();
        if (enabledOnly)
            q = q.Where(s => s.Enabled);
        return await q.OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<Dictionary<int, int>> StreamCountsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Streams.GroupBy(s => s.ServerId).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
    }

    public async Task<MediaServer?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<bool> NameExistsAsync(string name, int? exceptId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Servers.AnyAsync(s => s.Name == name && s.Id != exceptId, ct);
    }

    public async Task<MediaServer> CreateAsync(ServerInput input, CancellationToken ct = default)
    {
        var server = new MediaServer { HookSecret = CredentialGenerator.Password(32) };
        input.ApplyTo(server);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        return server;
    }

    public async Task<MediaServer?> UpdateAsync(int id, ServerInput input, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null)
            return null;
        input.ApplyTo(server);
        await db.SaveChangesAsync(ct);
        return server;
    }

    public async Task<MediaServer?> RegenerateHookSecretAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null)
            return null;
        server.HookSecret = CredentialGenerator.Password(32);
        await db.SaveChangesAsync(ct);
        return server;
    }

    /// <summary>Deletes a server. Refused while streams still run on it.</summary>
    public async Task<string?> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var server = await db.Servers.Include(s => s.Streams).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null)
            return "Server niet gevonden.";
        if (server.Streams.Count > 0)
            return $"Er staan nog {server.Streams.Count} stream(s) op deze server. Verplaats of verwijder die eerst.";
        db.Servers.Remove(server);
        await db.SaveChangesAsync(ct);
        status.Forget(id);
        return null;
    }

    /// <summary>
    /// Checks the API connection and, when it works, applies all streams of the server right away.
    /// Because MediaMTX authorizes API calls through the panel's hook, a successful call also proves
    /// that the hook address and secret are configured correctly on the server.
    /// </summary>
    public async Task<ServerTestResult> TestAndSyncAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var server = await db.Servers.AsNoTracking().Include(s => s.Streams).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null)
            return new(false, "Server niet gevonden.");

        var mtx = clients.Create(server);
        try
        {
            var info = await mtx.GetInfoAsync(ct);
            await sync.SyncServerAsync(mtx, server, ct);
            return new(true, $"Verbonden met MediaMTX {info?.Version}. {server.Streams.Count} stream(s) gesynchroniseerd.");
        }
        catch (MediaMtxException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            status.Report(id, false, null, ex.Message);
            return new(false, "MediaMTX weigert de API-gegevens. Controleer of authHTTPAddress op deze server naar het paneel wijst " +
                              "met het juiste geheim, en of de API-gebruiker en het wachtwoord kloppen.");
        }
        catch (Exception ex) when (ex is HttpRequestException or MediaMtxException or TaskCanceledException)
        {
            status.Report(id, false, null, ex.Message);
            return new(false, $"Geen verbinding met {server.ApiUrl}: {ex.Message}");
        }
    }
}
