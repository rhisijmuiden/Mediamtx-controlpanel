using MediaMtxPanel.Components.Shared;
using MediaMtxPanel.Data;
using MediaMtxPanel.Options;
using MediaMtxPanel.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaMtxPanel.Tests;

public class StreamStatsTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 30, DateTimeKind.Utc);
    private static long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

    private static async Task<(int A, int B)> TwoStreamsAsync(PanelFactory factory)
    {
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var server = await db.Servers.FirstAsync();
        var a = new StreamDefinition { ServerId = server.Id, Name = "a", DisplayName = "A", WatchSlug = "slug-a" };
        var b = new StreamDefinition { ServerId = server.Id, Name = "b", DisplayName = "B", WatchSlug = "slug-b" };
        db.Streams.AddRange(a, b);
        await db.SaveChangesAsync();
        return (a.Id, b.Id);
    }

    [Fact]
    public async Task Report_adds_streams_per_round_and_buckets_per_minute()
    {
        using var factory = new PanelFactory();
        var (a, b) = await TwoStreamsAsync(factory);
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var t1 = Unix(Now.AddMinutes(-10));
            var t2 = t1 + 60;
            db.StatSamples.AddRange(
                new StreamStatSample { StreamId = a, Timestamp = t1, Seconds = 60, Viewers = 3, BytesIn = 6000, BytesOut = 18000 },
                new StreamStatSample { StreamId = b, Timestamp = t1, Seconds = 60, Viewers = 2, BytesIn = 600, BytesOut = 1200 },
                new StreamStatSample { StreamId = a, Timestamp = t2, Seconds = 60, Viewers = 1, BytesIn = 6000, BytesOut = 6000 },
                // Too old for the last hour.
                new StreamStatSample { StreamId = a, Timestamp = Unix(Now.AddHours(-2)), Seconds = 60, Viewers = 50, BytesOut = 1 });
            await db.SaveChangesAsync();
        }

        var stats = factory.Services.GetRequiredService<StreamStatsService>();
        var report = await stats.GetAsync([a, b], StatsRange.Find("1h"), Now);

        Assert.Equal(60, report.Buckets.Count);
        Assert.Equal(5, report.PeakViewers); // 3 + 2 in the same round
        var first = report.Buckets.Single(x => x.PeakViewers == 5);
        Assert.Equal(19200, first.BytesOut);
        Assert.Equal(320, first.OutBytesPerSec);
        Assert.Equal(5, first.AvgViewers);

        var sa = report.Streams.Single(s => s.StreamId == a);
        Assert.Equal(3, sa.PeakViewers);
        Assert.Equal((3 + 1) * 60 / 3600.0, sa.ViewerHours, 6);
        Assert.Equal(24000, sa.BytesOut);
        Assert.Equal(2 * 60 / 3600.0, sa.LiveHours, 6);

        // Only the requested streams are counted.
        var onlyB = await stats.GetAsync([b], StatsRange.Find("1h"), Now);
        Assert.Equal(2, onlyB.PeakViewers);
        Assert.Equal(1200, onlyB.BytesOut);
    }

    [Fact]
    public async Task Deleting_a_stream_deletes_its_statistics()
    {
        using var factory = new PanelFactory();
        var (a, _) = await TwoStreamsAsync(factory);
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.StatSamples.Add(new StreamStatSample { StreamId = a, Timestamp = Unix(Now), Seconds = 60, Viewers = 1 });
        await db.SaveChangesAsync();

        await db.Streams.Where(s => s.Id == a).ExecuteDeleteAsync();
        Assert.Equal(0, await db.StatSamples.CountAsync());
    }

    [Fact]
    public void Collector_counts_traffic_since_the_previous_round()
    {
        var collector = new StreamStatsCollector(null!, null!,
            Microsoft.Extensions.Options.Options.Create(new MediaMtxOptions { StatisticsIntervalSeconds = 60 }),
            NullLogger<StreamStatsCollector>.Instance);
        MtxPath Path(ulong rx, ulong tx, int viewers) => new()
        {
            Ready = true, InboundBytes = rx, OutboundBytes = tx,
            Readers = Enumerable.Range(0, viewers).Select(i => new MtxConnRef("webrtcSession", $"{i}")).ToList(),
        };

        var first = collector.Measure(1, Path(1000, 5000, 2), 1000);
        Assert.Equal((0, 0, 2, 60), (first.BytesIn, first.BytesOut, first.Viewers, first.Seconds));

        var second = collector.Measure(1, Path(4000, 9000, 4), 1059);
        Assert.Equal((3000, 4000, 4, 59), (second.BytesIn, second.BytesOut, second.Viewers, second.Seconds));

        // The path restarted: counters began again from zero.
        var restarted = collector.Measure(1, Path(500, 700, 1), 1120);
        Assert.Equal((500, 700), (restarted.BytesIn, restarted.BytesOut));

        // A long gap is not spread over one sample.
        var afterGap = collector.Measure(1, Path(9000, 9000, 1), 5000);
        Assert.Equal((0, 0, 60), (afterGap.BytesIn, afterGap.BytesOut, afterGap.Seconds));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(3, 4)]
    [InlineData(5, 8)]
    [InlineData(37, 40)]
    [InlineData(1_000_000, 1_000_000)]
    [InlineData(1_300_000, 2_000_000)]
    public void Chart_scale_is_a_round_number(double top, double expected) =>
        Assert.Equal(expected, TimeChart.NiceCeiling(top), 6);
}
