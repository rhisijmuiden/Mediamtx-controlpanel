using System.Globalization;
using System.Security.Claims;
using System.Text;
using MediaMtxPanel.Services;

namespace MediaMtxPanel.Endpoints;

/// <summary>CSV download of the statistics shown on the statistics pages.</summary>
public static class StatisticsEndpoints
{
    public static IEndpointRouteBuilder MapStatisticsEndpoints(this IEndpointRouteBuilder app)
    {
        // Same streams as the pages: administrators see all, everyone else only the streams they own.
        app.MapGet("/api/statistics/export.csv", async (string? range, int? stream, ClaimsPrincipal user,
            StreamService streams, StreamStatsService stats, CancellationToken ct) =>
        {
            var visible = await streams.ListAsync(user, ct);
            if (stream is not null)
                visible = visible.Where(s => s.Id == stream).ToList();
            if (stream is not null && visible.Count == 0)
                return Results.NotFound();

            var r = StatsRange.Find(range);
            var report = await stats.GetAsync(visible.Select(s => s.Id).ToList(), r, ct: ct);

            var csv = new StringBuilder("start_utc,peak_viewers,avg_viewers,bytes_in,bytes_out,avg_bitrate_in_bps,avg_bitrate_out_bps\n");
            foreach (var b in report.Buckets)
                csv.Append(CultureInfo.InvariantCulture,
                    $"{b.Start:yyyy-MM-ddTHH:mm:ssZ},{b.PeakViewers},{b.AvgViewers:0.###},{b.BytesIn},{b.BytesOut},{b.InBytesPerSec * 8:0},{b.OutBytesPerSec * 8:0}\n");

            var name = stream is null ? "all-streams" : FileSafe(visible[0].Name);
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"statistics-{name}-{r.Key}.csv");
        }).RequireAuthorization();
        return app;
    }

    private static string FileSafe(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
}
