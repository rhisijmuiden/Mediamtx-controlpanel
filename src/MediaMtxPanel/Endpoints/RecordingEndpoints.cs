using System.Globalization;
using MediaMtxPanel.Services;

namespace MediaMtxPanel.Endpoints;

/// <summary>
/// Streams a recording from the MediaMTX playback server to the browser, for the players and download
/// buttons in the panel. Only signed-in users who manage the stream (administrators and its owner) get it.
/// </summary>
public static class RecordingEndpoints
{
    public static IEndpointRouteBuilder MapRecordingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/recordings/{streamId:int}", async (
            int streamId, string? start, string? duration, bool? download,
            HttpContext http, StreamService streams, RecordingService recordings, ILoggerFactory loggerFactory) =>
        {
            var ct = http.RequestAborted;
            if (!DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var from)
                || !double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                return Results.BadRequest();

            var stream = await streams.GetAsync(http.User, streamId, ct);
            if (stream is null)
                return Results.NotFound();

            var recording = new Recording(from, TimeSpan.FromSeconds(seconds));
            HttpResponseMessage upstream;
            try
            {
                upstream = await recordings.OpenAsync(stream, recording, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or MediaMtxException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                loggerFactory.CreateLogger("Recordings").LogWarning("Could not open recording of {Path}: {Message}", stream.Name, ex.Message);
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            http.Response.RegisterForDispose(upstream);
            var file = download == true ? RecordingService.FileName(stream, recording) : null;
            return Results.Stream(await upstream.Content.ReadAsStreamAsync(ct), "video/mp4", file);
        }).RequireAuthorization();
        return app;
    }
}
