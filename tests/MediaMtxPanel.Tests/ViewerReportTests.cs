using System.Globalization;
using System.Net;
using System.Text;
using MediaMtxPanel.Data;
using MediaMtxPanel.Endpoints;
using MediaMtxPanel.Options;
using MediaMtxPanel.Services;
using MediaMtxPanel.Services.Localization;
using MediaMtxPanel.Services.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaMtxPanel.Tests;

public class ViewerReportTests
{
    private static readonly DateOnly Day1 = new(2026, 10, 5); // a Monday
    private static readonly DateOnly Day2 = new(2026, 10, 6);

    private static long At(DateOnly day, int hour, int minute = 0) =>
        new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero).ToUnixTimeSeconds();

    private static async Task<(ViewerReportService Reports, IDbContextFactory<ApplicationDbContext> Db, int A, int B)> SetupAsync(PanelFactory factory)
    {
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var server = await db.Servers.FirstAsync();
        var a = new StreamDefinition { ServerId = server.Id, Name = "a", DisplayName = "Stream A", WatchSlug = "slug-a" };
        var b = new StreamDefinition { ServerId = server.Id, Name = "b", DisplayName = "Stream B", WatchSlug = "slug-b" };
        db.Streams.AddRange(a, b);
        await db.SaveChangesAsync();

        db.StatSamples.AddRange(
            // Day 1, 20:00: A has 3 viewers (2 new), B has 2 (2 new) → 5 at once.
            new StreamStatSample { StreamId = a.Id, Timestamp = At(Day1, 20), Seconds = 60, Viewers = 3, NewViewers = 2, BytesOut = 1000 },
            new StreamStatSample { StreamId = b.Id, Timestamp = At(Day1, 20), Seconds = 60, Viewers = 2, NewViewers = 2, BytesOut = 500 },
            // 20:01: A has 4 (1 new), B is offline.
            new StreamStatSample { StreamId = a.Id, Timestamp = At(Day1, 20, 1), Seconds = 60, Viewers = 4, NewViewers = 1, BytesOut = 2000 },
            // Day 2: A has 1 viewer.
            new StreamStatSample { StreamId = a.Id, Timestamp = At(Day2, 9), Seconds = 60, Viewers = 1, NewViewers = 1, BytesOut = 100 });
        await db.SaveChangesAsync();

        var reports = factory.Services.GetRequiredService<ViewerReportService>();
        reports.TimeZone = TimeZoneInfo.Utc;
        return (reports, dbFactory, a.Id, b.Id);
    }

    private static async Task AddVisitAsync(IDbContextFactory<ApplicationDbContext> dbFactory, DateOnly day, int stream, string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.ViewerVisits.Add(new ViewerVisit { Day = day, StreamId = stream, ViewerKey = key });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Rollup_builds_daily_rows_per_stream_and_for_all_streams()
    {
        using var factory = new PanelFactory();
        var (reports, dbFactory, a, b) = await SetupAsync(factory);
        await AddVisitAsync(dbFactory, Day1, a, "viewer1");
        await AddVisitAsync(dbFactory, Day1, a, "viewer2");
        await AddVisitAsync(dbFactory, Day1, b, "viewer1"); // the same person on another stream

        await reports.RollupAsync(Day1);
        await reports.RollupAsync(Day1); // recalculating replaces, it does not add up

        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.ViewerDailyStats.Where(x => x.Day == Day1).ToListAsync();
        Assert.Equal(3, rows.Count);

        var rowA = rows.Single(x => x.StreamId == a);
        Assert.Equal(("Stream A", 4, 3, 2, (3 + 4) * 60L, 120L, 3000L),
            (rowA.StreamName, rowA.PeakViewers, rowA.Sessions, rowA.UniqueViewers, rowA.ViewerSeconds, rowA.LiveSeconds, rowA.BytesOut));

        var all = rows.Single(x => x.StreamId == ViewerDailyStat.AllStreams);
        Assert.Equal(5, all.PeakViewers);      // 3 + 2 in the same round, not max(4, 2)
        Assert.Equal(5, all.Sessions);
        Assert.Equal(2, all.UniqueViewers);    // viewer1 counts once over both streams
        Assert.Equal(120, all.LiveSeconds);    // something was live for two minutes
        Assert.Equal(3500, all.BytesOut);
    }

    [Fact]
    public async Task Report_groups_days_and_keeps_history_after_samples_and_stream_are_gone()
    {
        using var factory = new PanelFactory();
        var (reports, dbFactory, a, b) = await SetupAsync(factory);
        await AddVisitAsync(dbFactory, Day1, a, "viewer1");
        await AddVisitAsync(dbFactory, Day2, a, "viewer1");
        await AddVisitAsync(dbFactory, Day2, a, "viewer3");
        await reports.RollupAsync(Day1);
        await reports.RollupAsync(Day2);

        // The statistics retention deletes the samples, and stream B is deleted.
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.StatSamples.ExecuteDeleteAsync();
            await db.Streams.Where(s => s.Id == b).ExecuteDeleteAsync();
        }
        await reports.RollupAsync(Day1); // nothing left to measure: the rolled-up day stays

        var daily = await reports.GetReportAsync(Day1, Day1.AddDays(6), ReportGrouping.Day, null);
        Assert.Equal(7, daily.Periods.Count);
        Assert.Equal(5, daily.Periods[0].Totals.PeakViewers);
        Assert.Equal(1, daily.Periods[1].Totals.PeakViewers);
        Assert.Equal(ViewerTotals.Empty, daily.Periods[2].Totals);
        Assert.Equal(5, daily.Totals.PeakViewers);
        Assert.Equal(6, daily.Totals.Sessions);
        Assert.Equal(2, daily.Totals.UniqueViewers); // viewer1 on both days counts once
        Assert.Equal((3 + 4 + 2 + 1) * 60L, daily.Totals.ViewerSeconds);

        var deleted = daily.Streams.Single(s => s.StreamId == b);
        Assert.False(deleted.Exists);
        Assert.Equal("Stream B", deleted.Name);
        Assert.Equal(daily.Streams[0].StreamId, a); // most watched first

        var weekly = await reports.GetReportAsync(Day1, Day1.AddDays(13), ReportGrouping.Week, null);
        Assert.Equal(2, weekly.Periods.Count);
        Assert.Equal((Day1, Day1.AddDays(6)), (weekly.Periods[0].Start, weekly.Periods[0].End));
        Assert.Equal(6, weekly.Periods[0].Totals.Sessions);
        Assert.Equal(2, weekly.Periods[0].Totals.UniqueViewers);

        var monthly = await reports.GetReportAsync(new DateOnly(2026, 9, 15), new DateOnly(2026, 10, 31), ReportGrouping.Month, a);
        Assert.Equal(2, monthly.Periods.Count);
        Assert.Equal(new DateOnly(2026, 9, 15), monthly.Periods[0].Start); // clipped to the chosen range
        Assert.Equal(4, monthly.Periods[1].Totals.PeakViewers);           // only stream A
        Assert.Equal("Stream A", monthly.StreamName);
        Assert.Single(monthly.Streams);

        var list = await reports.ListHistoryStreamsAsync();
        Assert.Contains(list, s => s.Id == b && !s.Exists && s.Name == "Stream B");
    }

    [Fact]
    public async Task Visits_are_counted_once_per_day_and_store_no_ip_address()
    {
        using var factory = new PanelFactory();
        var (reports, dbFactory, a, _) = await SetupAsync(factory);
        reports.TimeZone = TimeZoneInfo.Local;

        await reports.RecordVisitAsync(a, "203.0.113.7", null);
        await reports.RecordVisitAsync(a, "203.0.113.7", null);
        await reports.RecordVisitAsync(a, "203.0.113.8", null);
        await reports.RecordVisitAsync(a, "203.0.113.8", "ticket-token");

        await using var db = await dbFactory.CreateDbContextAsync();
        var keys = await db.ViewerVisits.Select(v => v.ViewerKey).ToListAsync();
        Assert.Equal(3, keys.Count);
        Assert.All(keys, k => Assert.DoesNotContain("203.0.113", k));
        Assert.Equal(reports.ViewerKey("203.0.113.7", null), reports.ViewerKey("203.0.113.7", null));
    }

    [Fact]
    public void Collector_counts_viewers_that_started_watching()
    {
        var platformViewers = new MediaMtxPanel.Services.Restream.PlatformViewerService(null!, null!, null!, null!,
            NullLogger<MediaMtxPanel.Services.Restream.PlatformViewerService>.Instance);
        var collector = new StreamStatsCollector(null!, null!, platformViewers,
            Microsoft.Extensions.Options.Options.Create(new MediaMtxOptions { StatisticsIntervalSeconds = 60 }),
            NullLogger<StreamStatsCollector>.Instance);
        MtxPath Path(params string[] ids) => new() { Ready = true, Readers = ids.Select(i => new MtxConnRef("webrtcSession", i)).ToList() };

        Assert.Equal(2, collector.Measure(1, Path("x", "y"), 1000).NewViewers);
        Assert.Equal(1, collector.Measure(1, Path("x", "y", "z"), 1060).NewViewers);
        Assert.Equal(1, collector.Measure(1, Path("z", "w"), 1120).NewViewers);
        Assert.Equal(0, collector.Measure(1, Path("z"), 1180).NewViewers);
    }

    [Fact]
    public async Task Pdf_and_csv_contain_the_report()
    {
        using var factory = new PanelFactory();
        var (reports, _, a, _) = await SetupAsync(factory);
        await reports.RollupAsync(Day1);
        await reports.RollupAsync(Day2);
        var report = await reports.GetReportAsync(Day1, Day2, ReportGrouping.Day, null);

        var csv = ReportEndpoints.PeriodsCsv(report).Split('\n');
        Assert.StartsWith("period_start,period_end,peak_viewers", csv[0]);
        Assert.StartsWith("2026-10-05,2026-10-05,5,", csv[1]);
        Assert.Contains($"{a},Stream A,no,4,", ReportEndpoints.StreamsCsv(report));

        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("nl-NL");
        try
        {
            var pdf = ViewerReportPdf.Render(report, "Test (panel)", new TextLocalizer(), new DateTime(2026, 10, 20, 8, 0, 0));
            var text = Encoding.Latin1.GetString(pdf);
            Assert.StartsWith("%PDF-1.4", text);
            Assert.EndsWith("%%EOF\n", text);
            Assert.Contains("(Kijkersrapportage) Tj", text);
            Assert.Contains("(Stream A) Tj", text);
            Assert.Contains("(Test \\(panel\\) \xb7 Kijkersrapportage) Tj", text); // escaped brackets, WinAnsi middle dot

            // The cross-reference table points at the objects.
            var xref = long.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
            Assert.StartsWith("xref", text[(int)xref..]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task Report_downloads_are_for_administrators_only()
    {
        using var factory = new PanelFactory();
        foreach (var url in new[] { "/api/reports/viewers.csv", "/api/reports/viewers.pdf?from=2026-10-01&to=2026-10-07&group=week" })
        {
            var anonymous = await factory.Client().GetAsync(url);
            Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
        }
    }

    [Fact]
    public void Pdf_text_is_measured_and_shortened()
    {
        Assert.Equal(5.56 * 3, PdfWriter.Measure("123", 10), 3);
        var fitted = PdfWriter.Fit("A very long stream name that does not fit", 60, 10);
        Assert.EndsWith("…", fitted);
        Assert.True(PdfWriter.Measure(fitted, 10) <= 60);
    }
}
