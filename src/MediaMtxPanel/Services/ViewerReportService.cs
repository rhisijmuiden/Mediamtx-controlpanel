using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaMtxPanel.Data;
using MediaMtxPanel.Options;
using MediaMtxPanel.Services.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Services;

/// <summary>How the viewer report groups days.</summary>
public enum ReportGrouping
{
    Day,
    Week,
    Month,
}

/// <summary>Viewer totals of one row of the report: a period, a stream, or everything.</summary>
public record ViewerTotals(int PeakViewers, int UniqueViewers, int Sessions, long ViewerSeconds, long LiveSeconds, long BytesIn, long BytesOut)
{
    public static readonly ViewerTotals Empty = new(0, 0, 0, 0, 0, 0, 0);

    public double WatchHours => ViewerSeconds / 3600.0;
    public double LiveHours => LiveSeconds / 3600.0;

    /// <summary>Average time one view lasted; 0 when nobody watched.</summary>
    public TimeSpan AvgViewDuration => Sessions > 0 ? TimeSpan.FromSeconds(ViewerSeconds / (double)Sessions) : TimeSpan.Zero;

    /// <summary>Average viewers while the stream was live.</summary>
    public double AvgViewers => LiveSeconds > 0 ? ViewerSeconds / (double)LiveSeconds : 0;
}

/// <summary>A period (day, week or month) of the report; <see cref="End"/> is the last day.</summary>
public record ReportPeriod(DateOnly Start, DateOnly End, ViewerTotals Totals);

public record ReportStream(int StreamId, string Name, bool Exists, ViewerTotals Totals);

public record ViewerReport(DateOnly From, DateOnly To, ReportGrouping Grouping, int? StreamId, string StreamName,
    List<ReportPeriod> Periods, List<ReportStream> Streams, ViewerTotals Totals)
{
    public bool IsEmpty => Totals.ViewerSeconds == 0 && Totals.LiveSeconds == 0 && Totals.UniqueViewers == 0;
}

/// <summary>
/// Long-term viewer history. Rolls the statistics samples up into one <see cref="ViewerDailyStat"/> per
/// stream per day (plus one for all streams), records unique viewers from the auth hook, and builds the
/// viewer report from both. Days are days in the server's time zone, like the times shown in the panel.
/// </summary>
public class ViewerReportService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    PanelSettingsStore settings,
    IOptions<MediaMtxOptions> options,
    ILogger<ViewerReportService> logger)
{
    private readonly ConcurrentDictionary<string, byte> _seenToday = new();
    private DateOnly _seenDay;
    private byte[]? _hashKey;
    private readonly Lock _keyGate = new();

    /// <summary>Time zone of the report's days; settable for tests.</summary>
    internal TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZone));

    /// <summary>Unix seconds at the start (00:00 local) of <paramref name="day"/>.</summary>
    internal long DayStart(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, TimeZone)).ToUnixTimeSeconds();
    }

    // --- Unique viewers ---

    /// <summary>
    /// Remembers that a viewer started watching <paramref name="streamId"/> today. Called by the auth hook for
    /// every allowed read, so repeat calls (HLS asks for every segment) are answered from memory.
    /// Never throws: counting viewers must not get in the way of watching.
    /// </summary>
    public async Task RecordVisitAsync(int streamId, string? ip, string? ticket, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ip) && string.IsNullOrEmpty(ticket))
            return;
        try
        {
            var today = Today;
            if (_seenDay != today)
            {
                _seenToday.Clear();
                _seenDay = today;
            }
            var key = ViewerKey(ip, ticket);
            if (!_seenToday.TryAdd($"{streamId}|{key}", 0))
                return;

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT OR IGNORE INTO ViewerVisits (Day, StreamId, ViewerKey) VALUES ({today}, {streamId}, {key})", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record a viewer of stream {StreamId}", streamId);
        }
    }

    /// <summary>
    /// A keyed hash of the viewer's IP address and ticket: the same viewer always gives the same key, but the
    /// IP address cannot be read back from it. The key is a random secret of this installation.
    /// </summary>
    internal string ViewerKey(string? ip, string? ticket)
    {
        using var hmac = new HMACSHA256(HashKey());
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ip}|{ticket}"));
        return Convert.ToHexStringLower(hash, 0, 12);
    }

    private byte[] HashKey()
    {
        if (_hashKey is not null)
            return _hashKey;
        lock (_keyGate)
        {
            var secret = settings.Current.ViewerKeySecret;
            if (string.IsNullOrEmpty(secret))
            {
                secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                settings.Save(settings.Current with { ViewerKeySecret = secret });
            }
            return _hashKey = Convert.FromBase64String(secret);
        }
    }

    // --- Daily roll-up ---

    /// <summary>
    /// Fills in the days that have statistics samples but no daily rows yet (e.g. right after an update),
    /// then recalculates yesterday and today. Run every hour by <see cref="ViewerReportRollup"/>.
    /// </summary>
    public async Task RollupRecentAsync(CancellationToken ct = default)
    {
        var today = Today;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var first = await db.StatSamples.MinAsync(x => (long?)x.Timestamp, ct);
            if (first is not null)
            {
                var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UnixEpoch.AddSeconds(first.Value), TimeZone));
                var done = (await db.ViewerDailyStats.Where(x => x.StreamId == ViewerDailyStat.AllStreams && x.Day >= firstDay)
                    .Select(x => x.Day).ToListAsync(ct)).ToHashSet();
                for (var day = firstDay; day < today.AddDays(-1); day = day.AddDays(1))
                    if (!done.Contains(day))
                        await RollupAsync(day, ct);
            }
        }
        await RollupAsync(today.AddDays(-1), ct);
        await RollupAsync(today, ct);
    }

    /// <summary>(Re)calculates the daily rows of <paramref name="day"/> from the samples and visits of that day.</summary>
    public async Task RollupAsync(DateOnly day, CancellationToken ct = default)
    {
        var start = DayStart(day);
        var end = DayStart(day.AddDays(1));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var samples = db.StatSamples.AsNoTracking().Where(x => x.Timestamp >= start && x.Timestamp < end);

        var perStream = await samples
            .GroupBy(x => x.StreamId)
            .Select(g => new
            {
                StreamId = g.Key,
                Peak = g.Max(x => x.Viewers),
                ViewerSeconds = g.Sum(x => (long)x.Viewers * x.Seconds),
                LiveSeconds = g.Sum(x => (long)x.Seconds),
                Sessions = g.Sum(x => x.NewViewers),
                BytesIn = g.Sum(x => x.BytesIn),
                BytesOut = g.Sum(x => x.BytesOut),
            })
            .ToListAsync(ct);

        // All streams of one collection round share the timestamp, so the busiest round is the overall peak.
        var overallPeak = await samples.GroupBy(x => x.Timestamp).Select(g => g.Sum(x => x.Viewers))
            .OrderByDescending(v => v).FirstOrDefaultAsync(ct);
        // Live time of all streams together: the time at least one stream was live.
        var overallLive = await samples.GroupBy(x => x.Timestamp).Select(g => g.Max(x => (long)x.Seconds)).SumAsync(ct);

        var visits = db.ViewerVisits.AsNoTracking().Where(v => v.Day == day);
        var uniquePerStream = await visits.GroupBy(v => v.StreamId)
            .Select(g => new { StreamId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.StreamId, x => x.Count, ct);
        var uniqueOverall = await visits.Select(v => v.ViewerKey).Distinct().CountAsync(ct);

        var existing = await db.ViewerDailyStats.Where(x => x.Day == day).ToListAsync(ct);
        if (perStream.Count == 0 && uniquePerStream.Count == 0)
            return;
        // Without samples (deleted by the statistics retention, or nothing was live) the figures rolled up
        // before are kept; only the unique viewers, which have their own table, are brought up to date.
        var haveSamples = perStream.Count > 0;

        var ids = perStream.Select(s => s.StreamId).Union(uniquePerStream.Keys).ToList();
        var names = await db.Streams.AsNoTracking().Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);

        ViewerDailyStat Row(int streamId)
        {
            var row = existing.FirstOrDefault(x => x.StreamId == streamId);
            if (row is null)
            {
                row = new ViewerDailyStat { Day = day, StreamId = streamId };
                db.ViewerDailyStats.Add(row);
                existing.Add(row);
            }
            return row;
        }

        foreach (var id in ids)
        {
            var row = Row(id);
            if (names.TryGetValue(id, out var name))
                row.StreamName = name;
            row.UniqueViewers = uniquePerStream.GetValueOrDefault(id);
            if (!haveSamples)
                continue;
            var s = perStream.FirstOrDefault(x => x.StreamId == id);
            row.PeakViewers = s?.Peak ?? 0;
            row.ViewerSeconds = s?.ViewerSeconds ?? 0;
            row.LiveSeconds = s?.LiveSeconds ?? 0;
            row.Sessions = s?.Sessions ?? 0;
            row.BytesIn = s?.BytesIn ?? 0;
            row.BytesOut = s?.BytesOut ?? 0;
        }

        var all = Row(ViewerDailyStat.AllStreams);
        all.UniqueViewers = uniqueOverall;
        if (haveSamples)
        {
            all.PeakViewers = overallPeak;
            all.ViewerSeconds = perStream.Sum(x => x.ViewerSeconds);
            all.LiveSeconds = overallLive;
            all.Sessions = perStream.Sum(x => x.Sessions);
            all.BytesIn = perStream.Sum(x => x.BytesIn);
            all.BytesOut = perStream.Sum(x => x.BytesOut);

            // Streams without samples or viewers this day any more (recalculated after a restore, say).
            var touched = ids.Append(ViewerDailyStat.AllStreams).ToHashSet();
            db.ViewerDailyStats.RemoveRange(existing.Where(x => !touched.Contains(x.StreamId) && x.Id != 0));
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes history older than <see cref="MediaMtxOptions.ReportRetentionDays"/> (0 keeps everything).</summary>
    public async Task CleanupAsync(CancellationToken ct = default)
    {
        var days = options.Value.ReportRetentionDays;
        if (days <= 0)
            return;
        var cutoff = Today.AddDays(-days);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ViewerDailyStats.Where(x => x.Day < cutoff).ExecuteDeleteAsync(ct);
        rows += await db.ViewerVisits.Where(x => x.Day < cutoff).ExecuteDeleteAsync(ct);
        if (rows > 0)
            logger.LogInformation("Deleted {Count} viewer history row(s) older than {Days} days", rows, days);
    }

    // --- Report ---

    /// <summary>Streams that appear in the history, also deleted ones, for the stream picker.</summary>
    public async Task<List<(int Id, string Name, bool Exists)>> ListHistoryStreamsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var current = await db.Streams.AsNoTracking().Select(s => new { s.Id, s.DisplayName }).ToListAsync(ct);
        var history = await db.ViewerDailyStats.AsNoTracking()
            .Where(x => x.StreamId != ViewerDailyStat.AllStreams)
            .GroupBy(x => x.StreamId)
            .Select(g => new { Id = g.Key, Name = g.OrderByDescending(x => x.Day).Select(x => x.StreamName).First() })
            .ToListAsync(ct);
        var ids = current.Select(c => c.Id).ToHashSet();
        return current.Select(c => (c.Id, c.DisplayName, true))
            .Concat(history.Where(h => !ids.Contains(h.Id)).Select(h => (h.Id, h.Name, false)))
            .OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The viewer report from <paramref name="from"/> to <paramref name="to"/> (both included), for one stream
    /// or for all streams (<paramref name="streamId"/> null).
    /// </summary>
    public async Task<ViewerReport> GetReportAsync(DateOnly from, DateOnly to, ReportGrouping grouping, int? streamId, CancellationToken ct = default)
    {
        if (to < from)
            (from, to) = (to, from);
        // Today changes every minute; bring it up to date before reporting on it.
        if (to >= Today && from <= Today)
            await RollupAsync(Today, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var target = streamId ?? ViewerDailyStat.AllStreams;
        var rows = await db.ViewerDailyStats.AsNoTracking()
            .Where(x => x.Day >= from && x.Day <= to)
            .ToListAsync(ct);
        var visits = await db.ViewerVisits.AsNoTracking()
            .Where(v => v.Day >= from && v.Day <= to && (streamId == null || v.StreamId == streamId))
            .Select(v => new { v.Day, v.StreamId, v.ViewerKey })
            .ToListAsync(ct);

        var mine = rows.Where(x => x.StreamId == target).ToList();
        var periods = new List<ReportPeriod>();
        for (var start = PeriodStart(from, grouping); start <= to; start = NextPeriod(start, grouping))
        {
            var end = NextPeriod(start, grouping).AddDays(-1);
            var days = mine.Where(x => x.Day >= start && x.Day <= end).ToList();
            var unique = visits.Where(v => v.Day >= start && v.Day <= end).Select(v => v.ViewerKey).Distinct().Count();
            periods.Add(new ReportPeriod(start < from ? from : start, end > to ? to : end, Sum(days, unique)));
        }

        var current = await db.Streams.AsNoTracking().Select(s => new { s.Id, s.DisplayName }).ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
        var streams = rows.Where(x => x.StreamId != ViewerDailyStat.AllStreams && (streamId == null || x.StreamId == streamId))
            .GroupBy(x => x.StreamId)
            .Select(g =>
            {
                var unique = visits.Where(v => v.StreamId == g.Key).Select(v => v.ViewerKey).Distinct().Count();
                var exists = current.TryGetValue(g.Key, out var name);
                return new ReportStream(g.Key, exists ? name! : g.OrderByDescending(x => x.Day).First().StreamName, exists, Sum(g.ToList(), unique));
            })
            .OrderByDescending(s => s.Totals.ViewerSeconds).ThenByDescending(s => s.Totals.PeakViewers).ThenBy(s => s.Name)
            .ToList();

        var totalUnique = visits.Select(v => v.ViewerKey).Distinct().Count();
        var streamName = streamId is null ? "" :
            current.GetValueOrDefault(streamId.Value) ?? streams.FirstOrDefault()?.Name ?? $"#{streamId}";
        return new ViewerReport(from, to, grouping, streamId, streamName, periods, streams, Sum(mine, totalUnique));
    }

    private static ViewerTotals Sum(List<ViewerDailyStat> days, int unique) => days.Count == 0 && unique == 0
        ? ViewerTotals.Empty
        : new ViewerTotals(
            days.Count == 0 ? 0 : days.Max(d => d.PeakViewers),
            unique,
            days.Sum(d => d.Sessions),
            days.Sum(d => d.ViewerSeconds),
            days.Sum(d => d.LiveSeconds),
            days.Sum(d => d.BytesIn),
            days.Sum(d => d.BytesOut));

    /// <summary>First day of the period that contains <paramref name="day"/>; weeks start on Monday.</summary>
    public static DateOnly PeriodStart(DateOnly day, ReportGrouping grouping) => grouping switch
    {
        ReportGrouping.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        ReportGrouping.Month => new DateOnly(day.Year, day.Month, 1),
        _ => day,
    };

    private static DateOnly NextPeriod(DateOnly start, ReportGrouping grouping) => grouping switch
    {
        ReportGrouping.Week => start.AddDays(7),
        ReportGrouping.Month => start.AddMonths(1),
        _ => start.AddDays(1),
    };

    /// <summary>Label of a period as shown in the report, e.g. "12 Oct 2026", "Week 42, 2026" or "October 2026".</summary>
    public static string PeriodLabel(ReportPeriod p, ReportGrouping grouping, TextLocalizer l) => grouping switch
    {
        ReportGrouping.Week => l["Week {0}, {1}", ISOWeek.GetWeekOfYear(p.Start.ToDateTime(TimeOnly.MinValue)), ISOWeek.GetYear(p.Start.ToDateTime(TimeOnly.MinValue))],
        ReportGrouping.Month => p.Start.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
        _ => p.Start.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture),
    };
}

/// <summary>Rolls the statistics up into the viewer history every hour.</summary>
public class ViewerReportRollup(ViewerReportService reports, ILogger<ViewerReportRollup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            do
            {
                try
                {
                    await reports.RollupRecentAsync(stoppingToken);
                    await reports.CleanupAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Updating the viewer history failed");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
