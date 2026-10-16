using MediaMtxPanel.Data;
using MediaMtxPanel.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Services;

/// <summary>
/// MediaMTX keeps configuration changes made through the API in memory only, so after a
/// restart of a server the panel has to put its streams back. This service reconciles every
/// enabled server periodically.
/// </summary>
public class MediaMtxSyncService(
    IServiceProvider services,
    IOptions<MediaMtxOptions> options,
    MediaMtxStatus status,
    ILogger<MediaMtxSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.SyncIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        // Give Kestrel a moment to start listening: MediaMTX calls back into the panel to authorize API requests.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        do
        {
            try
            {
                await SyncAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MediaMTX sync failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAllAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var clients = scope.ServiceProvider.GetRequiredService<MediaMtxClientFactory>();

        List<MediaServer> servers;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            servers = await db.Servers.AsNoTracking().Include(s => s.Streams).Where(s => s.Enabled).ToListAsync(ct);

        // Servers are independent; one that is down must not hold up the others.
        await Task.WhenAll(servers.Select(async server =>
        {
            try
            {
                await SyncServerAsync(clients.Create(server), server, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or MediaMtxException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                status.Report(server.Id, false, null, ex.Message);
                logger.LogWarning("Sync with server {Server} failed: {Message}", server.Name, ex.Message);
            }
        }));
    }

    public async Task SyncServerAsync(MediaMtxClient mtx, MediaServer server, CancellationToken ct)
    {
        var info = await mtx.GetInfoAsync(ct);
        var configs = (await mtx.ListPathConfigsAsync(ct))
            .Where(c => c.Name is not null)
            .ToDictionary(c => c.Name!);

        var changes = 0;
        foreach (var s in server.Streams)
        {
            configs.TryGetValue(s.Name, out var current);
            if (!s.Enabled)
            {
                if (current is not null)
                {
                    await mtx.DeletePathConfigAsync(s.Name, ct);
                    changes++;
                }
                continue;
            }

            var desired = StreamService.ToConfig(s);
            if (current is null)
            {
                await mtx.AddPathConfigAsync(s.Name, desired, ct);
                changes++;
            }
            else if (!StreamService.Matches(current, desired))
            {
                await mtx.ReplacePathConfigAsync(s.Name, desired, ct);
                changes++;
            }
        }

        if (server.RemoveUnmanagedPaths)
        {
            var known = server.Streams.Select(s => s.Name).ToHashSet();
            foreach (var name in configs.Keys.Where(n => !known.Contains(n)))
            {
                await mtx.DeletePathConfigAsync(name, ct);
                changes++;
            }
        }

        if (changes > 0)
            logger.LogInformation("Applied {Count} change(s) to server {Server}", changes, server.Name);
        status.Report(server.Id, true, info?.Version, null);
    }
}
