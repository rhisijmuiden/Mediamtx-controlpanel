using MediaMtxPanel.Data;
using MediaMtxPanel.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Services;

/// <summary>
/// Stores the viewers and traffic of every live stream once per <see cref="MediaMtxOptions.StatisticsIntervalSeconds"/>
/// for the statistics pages, and deletes samples older than <see cref="MediaMtxOptions.StatisticsRetentionDays"/>.
/// MediaMTX only reports byte counters since the path came up, so the traffic per period is the
/// difference with the previous round.
/// </summary>
public class StreamStatsCollector(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    LiveStatusReader liveReader,
    Restream.PlatformViewerService platformViewers,
    IOptions<MediaMtxOptions> options,
    ILogger<StreamStatsCollector> logger) : BackgroundService
{
    private record Counters(long Timestamp, ulong In, ulong Out);

    private readonly Dictionary<int, Counters> _previous = [];
    private DateTime _lastCleanup = DateTime.MinValue;

    private int IntervalSeconds => Math.Max(10, options.Value.StatisticsIntervalSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(IntervalSeconds));
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        do
        {
            try
            {
                await CollectAsync(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), stoppingToken);
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Collecting stream statistics failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One collection round at <paramref name="now"/> (Unix seconds).</summary>
    public async Task CollectAsync(long now, CancellationToken ct)
    {
        List<StreamDefinition> streams;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            streams = await db.Streams.AsNoTracking().Include(s => s.Server)
                .Where(s => s.Enabled && s.Server!.Enabled).ToListAsync(ct);

        var snapshot = await liveReader.ReadAsync(streams.Select(s => s.Server!), includeInfo: false, ct);
        var samples = new List<StreamStatSample>();
        foreach (var stream in streams)
        {
            var path = snapshot.Find(stream);
            if (path is null || !path.IsLive)
            {
                _previous.Remove(stream.Id);
                continue;
            }
            samples.Add(Measure(stream.Id, path, now));
        }

        // Streams that were deleted or disabled since the last round.
        var active = streams.Select(s => s.Id).ToHashSet();
        foreach (var id in _previous.Keys.Where(id => !active.Contains(id)).ToList())
            _previous.Remove(id);

        if (samples.Count == 0)
            return;
        await using var save = await dbFactory.CreateDbContextAsync(ct);
        save.StatSamples.AddRange(samples);
        await save.SaveChangesAsync(ct);
    }

    internal StreamStatSample Measure(int streamId, MtxPath path, long now)
    {
        var interval = IntervalSeconds;
        var sample = new StreamStatSample
        {
            StreamId = streamId,
            Timestamp = now,
            Seconds = interval,
            Viewers = path.Readers.Count,
            PlatformViewers = platformViewers.GetTotal(streamId),
        };

        // Only count traffic against a recent previous round; after a gap (panel or server down) the
        // counters cannot be split over the missing time. Lower counters mean the path restarted.
        if (_previous.TryGetValue(streamId, out var prev) && now > prev.Timestamp && now - prev.Timestamp <= 3 * interval)
        {
            sample.Seconds = (int)(now - prev.Timestamp);
            sample.BytesIn = (long)(path.Received >= prev.In ? path.Received - prev.In : path.Received);
            sample.BytesOut = (long)(path.Sent >= prev.Out ? path.Sent - prev.Out : path.Sent);
        }
        _previous[streamId] = new Counters(now, path.Received, path.Sent);
        return sample;
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastCleanup < TimeSpan.FromHours(1))
            return;
        _lastCleanup = DateTime.UtcNow;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, options.Value.StatisticsRetentionDays)).ToUnixTimeSeconds();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deleted = await db.StatSamples.Where(x => x.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        if (deleted > 0)
            logger.LogInformation("Deleted {Count} statistics sample(s) older than {Days} days", deleted, options.Value.StatisticsRetentionDays);
    }
}
