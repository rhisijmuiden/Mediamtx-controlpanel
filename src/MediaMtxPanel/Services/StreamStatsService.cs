using MediaMtxPanel.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Services;

/// <summary>A period the statistics pages can show, with the size of one chart bar.</summary>
public record StatsRange(string Key, string Label, TimeSpan Length, TimeSpan Bucket)
{
    public static readonly StatsRange[] All =
    [
        new("1h", "Last hour", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)),
        new("24h", "Last 24 hours", TimeSpan.FromHours(24), TimeSpan.FromMinutes(15)),
        new("7d", "Last 7 days", TimeSpan.FromDays(7), TimeSpan.FromHours(1)),
        new("30d", "Last 30 days", TimeSpan.FromDays(30), TimeSpan.FromHours(6)),
        new("90d", "Last 90 days", TimeSpan.FromDays(90), TimeSpan.FromDays(1)),
    ];

    public static StatsRange Default => All[1];

    public static StatsRange Find(string? key) => All.FirstOrDefault(r => r.Key == key) ?? Default;
}

/// <summary>Totals of one chart bar.</summary>
/// <param name="PeakViewers">Most viewers at one moment (summed over the streams).</param>
/// <param name="AvgViewers">Average viewers over the whole bar; time without samples counts as 0.</param>
public record StatsBucket(DateTime Start, int PeakViewers, double AvgViewers, long BytesIn, long BytesOut, int Seconds)
{
    public double InBytesPerSec => Seconds > 0 ? (double)BytesIn / Seconds : 0;
    public double OutBytesPerSec => Seconds > 0 ? (double)BytesOut / Seconds : 0;
}

public record StreamStatsSummary(int StreamId, int PeakViewers, double ViewerHours, long BytesIn, long BytesOut, double LiveHours);

public record StatsReport(StatsRange Range, DateTime From, DateTime To, List<StatsBucket> Buckets, List<StreamStatsSummary> Streams)
{
    public int PeakViewers => Buckets.Count == 0 ? 0 : Buckets.Max(b => b.PeakViewers);
    public double ViewerHours => Streams.Sum(s => s.ViewerHours);
    public long BytesIn => Streams.Sum(s => s.BytesIn);
    public long BytesOut => Streams.Sum(s => s.BytesOut);
    public double AvgViewers => (To - From).TotalHours > 0 ? ViewerHours / (To - From).TotalHours : 0;
    public double PeakOutBytesPerSec => Buckets.Count == 0 ? 0 : Buckets.Max(b => b.OutBytesPerSec);
    public bool IsEmpty => Streams.Count == 0;
}

/// <summary>Reads the samples of <see cref="StreamStatsCollector"/> back as chart data and per-stream totals.</summary>
public class StreamStatsService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>
    /// Statistics of <paramref name="streamIds"/> over <paramref name="range"/>, ending at
    /// <paramref name="now"/> (defaults to the current time). The caller decides which streams the user may see.
    /// </summary>
    public async Task<StatsReport> GetAsync(IReadOnlyCollection<int> streamIds, StatsRange range, DateTime? now = null, CancellationToken ct = default)
    {
        var bucketSeconds = (long)range.Bucket.TotalSeconds;
        // Align the bars to whole buckets, so the chart doesn't shift every time it is refreshed.
        var end = (new DateTimeOffset(now ?? DateTime.UtcNow).ToUnixTimeSeconds() / bucketSeconds + 1) * bucketSeconds;
        var start = end - (long)range.Length.TotalSeconds;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var samples = db.StatSamples.AsNoTracking()
            .Where(x => streamIds.Contains(x.StreamId) && x.Timestamp >= start && x.Timestamp < end);

        // All streams of one collection round share the timestamp, so a round is one moment in time.
        var rounds = await samples
            .GroupBy(x => x.Timestamp)
            .Select(g => new
            {
                Timestamp = g.Key,
                Viewers = g.Sum(x => x.Viewers),
                ViewerSeconds = g.Sum(x => (long)x.Viewers * x.Seconds),
                BytesIn = g.Sum(x => x.BytesIn),
                BytesOut = g.Sum(x => x.BytesOut),
            })
            .ToListAsync(ct);

        var perStream = await samples
            .GroupBy(x => x.StreamId)
            .Select(g => new
            {
                StreamId = g.Key,
                Peak = g.Max(x => x.Viewers),
                ViewerSeconds = g.Sum(x => (long)x.Viewers * x.Seconds),
                BytesIn = g.Sum(x => x.BytesIn),
                BytesOut = g.Sum(x => x.BytesOut),
                LiveSeconds = g.Sum(x => (long)x.Seconds),
            })
            .ToListAsync(ct);

        var byBucket = rounds.ToLookup(r => (r.Timestamp - start) / bucketSeconds);
        var buckets = new List<StatsBucket>();
        for (long i = 0, count = (end - start) / bucketSeconds; i < count; i++)
        {
            var items = byBucket[i].ToList();
            buckets.Add(new StatsBucket(
                DateTime.UnixEpoch.AddSeconds(start + i * bucketSeconds),
                items.Count == 0 ? 0 : items.Max(r => r.Viewers),
                (double)items.Sum(r => r.ViewerSeconds) / bucketSeconds,
                items.Sum(r => r.BytesIn),
                items.Sum(r => r.BytesOut),
                (int)bucketSeconds));
        }

        return new StatsReport(
            range,
            DateTime.UnixEpoch.AddSeconds(start),
            DateTime.UnixEpoch.AddSeconds(end),
            buckets,
            perStream
                .Select(s => new StreamStatsSummary(s.StreamId, s.Peak, s.ViewerSeconds / 3600.0, s.BytesIn, s.BytesOut, s.LiveSeconds / 3600.0))
                .OrderByDescending(s => s.ViewerHours).ThenByDescending(s => s.BytesOut)
                .ToList());
    }
}
