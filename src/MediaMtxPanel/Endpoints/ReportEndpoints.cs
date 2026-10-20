using System.Globalization;
using System.Text;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services;
using MediaMtxPanel.Services.Reports;
using MediaMtxPanel.Services.Localization;

namespace MediaMtxPanel.Endpoints;

/// <summary>Downloads of the viewer report (Reports in the menu) as CSV and PDF. Administrators only.</summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // part=periods (default): one row per day/week/month; part=streams: one row per stream.
        group.MapGet("/viewers.csv", async (string? from, string? to, string? group, int? stream, string? part,
            ViewerReportService reports, CancellationToken ct) =>
        {
            var report = await reports.GetReportAsync(ParseDay(from, reports.Today.AddDays(-29)), ParseDay(to, reports.Today),
                ParseGrouping(group), stream, ct);
            var csv = part == "streams" ? StreamsCsv(report) : PeriodsCsv(report);
            return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv", FileName(report, part == "streams" ? "streams" : null, "csv"));
        });

        group.MapGet("/viewers.pdf", async (string? from, string? to, string? group, int? stream,
            ViewerReportService reports, PanelSettingsStore settings, TextLocalizer l, CancellationToken ct) =>
        {
            var report = await reports.GetReportAsync(ParseDay(from, reports.Today.AddDays(-29)), ParseDay(to, reports.Today),
                ParseGrouping(group), stream, ct);
            var pdf = ViewerReportPdf.Render(report, settings.Current.AppName, l, DateTime.Now);
            return Results.File(pdf, "application/pdf", FileName(report, null, "pdf"));
        });
        return app;
    }

    internal static DateOnly ParseDay(string? value, DateOnly fallback) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : fallback;

    internal static ReportGrouping ParseGrouping(string? value) =>
        Enum.TryParse<ReportGrouping>(value, ignoreCase: true, out var g) ? g : ReportGrouping.Day;

    internal static string PeriodsCsv(ViewerReport report)
    {
        var csv = new StringBuilder("period_start,period_end,peak_viewers,unique_viewers,views,watch_hours,avg_view_minutes,avg_viewers,live_hours,bytes_in,bytes_out\n");
        foreach (var p in report.Periods)
            csv.Append(CultureInfo.InvariantCulture, $"{p.Start:yyyy-MM-dd},{p.End:yyyy-MM-dd},{Row(p.Totals)}\n");
        return csv.ToString();
    }

    internal static string StreamsCsv(ViewerReport report)
    {
        var csv = new StringBuilder("stream_id,stream,deleted,peak_viewers,unique_viewers,views,watch_hours,avg_view_minutes,avg_viewers,live_hours,bytes_in,bytes_out\n");
        foreach (var s in report.Streams)
            csv.Append(CultureInfo.InvariantCulture, $"{s.StreamId},{Quote(s.Name)},{(s.Exists ? "no" : "yes")},{Row(s.Totals)}\n");
        return csv.ToString();
    }

    private static string Row(ViewerTotals t) => string.Create(CultureInfo.InvariantCulture,
        $"{t.PeakViewers},{t.UniqueViewers},{t.Sessions},{t.WatchHours:0.##},{t.AvgViewDuration.TotalMinutes:0.#},{t.AvgViewers:0.##},{t.LiveHours:0.##},{t.BytesIn},{t.BytesOut}");

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static string FileName(ViewerReport report, string? suffix, string extension)
    {
        var who = report.StreamId is null ? "all-streams"
            : new string(report.StreamName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return $"viewer-report-{who}-{report.From:yyyy-MM-dd}-{report.To:yyyy-MM-dd}{(suffix is null ? "" : "-" + suffix)}.{extension}";
    }
}
