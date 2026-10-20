using System.ComponentModel.DataAnnotations;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services.Localization;
using MediaMtxPanel.Services.Setup;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Services;

public class ServerInput
{
    [Required(ErrorMessage = "Give the server a name."), StringLength(64)]
    public string Name { get; set; } = "";

    [StringLength(512)]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Enter the API URL.")]
    [RegularExpression(@"^https?://\S+$", ErrorMessage = "Use a URL such as http://10.0.0.5:9997.")]
    public string ApiUrl { get; set; } = "http://";

    [Required, StringLength(64)]
    public string ApiUser { get; set; } = "panel";

    [Required(ErrorMessage = "Enter an API password."), StringLength(128)]
    public string ApiPassword { get; set; } = CredentialGenerator.Password(24);

    [Required(ErrorMessage = "Enter the public address."), StringLength(256)]
    public string PublicHost { get; set; } = "";

    [Range(1, 65535)] public int RtspPort { get; set; } = 8554;
    [Range(1, 65535)] public int RtmpPort { get; set; } = 1935;
    [Range(1, 65535)] public int SrtPort { get; set; } = 8890;
    [Range(1, 65535)] public int HlsPort { get; set; } = 8888;
    [Range(1, 65535)] public int WebRtcPort { get; set; } = 8889;

    public bool HttpsUrls { get; set; }

    public ViewerRoute ViewerRoute { get; set; }

    [StringLength(256)]
    [RegularExpression(@"^https?://[^\s?#]+$", ErrorMessage = "Use a URL such as https://stream.example.com/hls.")]
    public string? PublicHlsUrl { get; set; }

    [StringLength(256)]
    [RegularExpression(@"^https?://[^\s?#]+$", ErrorMessage = "Use a URL such as https://stream.example.com/webrtc.")]
    public string? PublicWebRtcUrl { get; set; }

    [StringLength(256), Url(ErrorMessage = "Enter a full URL, for example http://10.0.0.21:9100/metrics.")]
    public string? MetricsUrl { get; set; }

    [StringLength(256)]
    [RegularExpression(@"^https?://\S+$", ErrorMessage = "Use a URL such as http://10.0.0.5:9996.")]
    public string? PlaybackUrl { get; set; }

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
        ViewerRoute = s.ViewerRoute,
        PublicHlsUrl = s.PublicHlsUrl,
        PublicWebRtcUrl = s.PublicWebRtcUrl,
        MetricsUrl = s.MetricsUrl,
        PlaybackUrl = s.PlaybackUrl,
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
        s.ViewerRoute = ViewerRoute;
        s.PublicHlsUrl = string.IsNullOrWhiteSpace(PublicHlsUrl) ? null : PublicHlsUrl.Trim().TrimEnd('/');
        s.PublicWebRtcUrl = string.IsNullOrWhiteSpace(PublicWebRtcUrl) ? null : PublicWebRtcUrl.Trim().TrimEnd('/');
        s.MetricsUrl = string.IsNullOrWhiteSpace(MetricsUrl) ? null : MetricsUrl.Trim();
        s.PlaybackUrl = string.IsNullOrWhiteSpace(PlaybackUrl) ? null : PlaybackUrl.Trim().TrimEnd('/');
        s.RemoveUnmanagedPaths = RemoveUnmanagedPaths;
        s.Enabled = Enabled;
    }
}

public record ServerTestResult(bool Ok, string Message);

/// <param name="Configured">MediaMTX on the server now uses the panel (even when the API test failed).</param>
public record SshConfigureResult(bool Ok, bool Configured, string Message);

/// <summary>Management of the MediaMTX servers the panel controls.</summary>
public class ServerService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    MediaMtxClientFactory clients,
    MediaMtxSyncService sync,
    MediaMtxStatus status,
    Endpoints.MediaProxyServers proxyServers,
    TextLocalizer L,
    SshServerSetup sshSetup)
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

    public async Task<MediaServer> CreateAsync(ServerInput input, string? hookSecret = null, CancellationToken ct = default)
    {
        var server = new MediaServer { HookSecret = hookSecret ?? CredentialGenerator.Password(32) };
        input.ApplyTo(server);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        proxyServers.Invalidate();
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
        proxyServers.Invalidate();
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
        proxyServers.Invalidate();
        return server;
    }

    /// <summary>Deletes a server. Refused while streams still run on it.</summary>
    public async Task<string?> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var server = await db.Servers.Include(s => s.Streams).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null)
            return L["Server not found."];
        if (server.Streams.Count > 0)
            return L["This server still has {0} stream(s). Move or delete them first.", server.Streams.Count];
        db.Servers.Remove(server);
        await db.SaveChangesAsync(ct);
        proxyServers.Invalidate();
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
            return new(false, L["Server not found."]);

        var mtx = clients.Create(server);
        try
        {
            var info = await mtx.GetInfoAsync(ct);
            await sync.SyncServerAsync(mtx, server, ct);
            return new(true, L["Connected to MediaMTX {0}. {1} stream(s) synchronized.", info?.Version, server.Streams.Count]);
        }
        catch (MediaMtxException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            status.Report(id, false, null, ex.Message);
            return new(false, L["MediaMTX rejects the API credentials. Check that authHTTPAddress on this server points to the panel with the correct secret, and that the API user and password are correct."]);
        }
        catch (Exception ex) when (ex is HttpRequestException or MediaMtxException or TaskCanceledException)
        {
            status.Report(id, false, null, ex.Message);
            return new(false, L["Cannot connect to {0}: {1}", server.ApiUrl, ex.Message]);
        }
    }

    /// <summary>
    /// Configures MediaMTX on the server over SSH so that it uses the panel (see <see cref="SshServerSetup"/>),
    /// then waits for the Control API to answer and synchronizes the server's streams.
    /// </summary>
    public async Task<SshConfigureResult> ConfigureOverSshAsync(int id, SshLoginInput login, string hookUrl,
        Action<string> log, CancellationToken ct = default)
    {
        var server = await GetAsync(id, ct);
        if (server is null)
            return new(false, false, L["Server not found."]);

        var setup = await sshSetup.ConfigureAsync(server, login, hookUrl, log, ct);
        if (!setup.Ok)
            return new(false, false, setup.Message);

        log($"Connecting to the Control API at {server.ApiUrl}…");
        ServerTestResult test = new(false, "");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            test = await TestAndSyncAsync(id, ct);
            if (test.Ok)
                return new(true, true, $"{setup.Message} {test.Message}");
        }
        return new(false, true, $"{setup.Message} " + L["But the panel cannot use the control API yet: {0} Check that port {1} on the server is reachable from the panel (firewall, NAT) and that the API URL is correct.", test.Message, new Uri(server.ApiUrl).Port]);
    }
}
