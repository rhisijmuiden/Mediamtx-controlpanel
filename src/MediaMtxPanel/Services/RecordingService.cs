using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MediaMtxPanel.Data;

namespace MediaMtxPanel.Services;

/// <summary>One continuous recording: MediaMTX joins segments (files) that follow each other without a gap.</summary>
public record Recording(DateTimeOffset Start, TimeSpan Duration)
{
    public DateTimeOffset End => Start + Duration;

    /// <summary>Query string the panel's recording endpoint and the playback server both understand.</summary>
    public string Query =>
        $"start={Uri.EscapeDataString(MediaMtxClient.RecordingTime(Start))}&duration={Duration.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Recordings of a stream. Listing and downloading go through the playback server of the MediaMTX server
/// the stream lives on (the panel signs in with the stream's viewer credentials, checked by the auth hook);
/// deleting goes through the Control API.
/// </summary>
public class RecordingService(IHttpClientFactory httpFactory, MediaMtxClientFactory clients, ILogger<RecordingService> logger)
{
    public const string HttpClientName = "mediamtx-playback";

    public async Task<List<Recording>> ListAsync(StreamDefinition stream, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var req = Request(stream, $"list?path={Uri.EscapeDataString(stream.Name)}");
        using var res = await httpFactory.CreateClient(HttpClientName).SendAsync(req, cts.Token);
        // MediaMTX answers 404 when the path has no recordings (yet).
        if (res.StatusCode == HttpStatusCode.NotFound)
            return [];
        await EnsureSuccess(res, cts.Token);
        var items = await res.Content.ReadFromJsonAsync<List<PlaybackListEntry>>(cts.Token) ?? [];
        return items
            .Select(i => new Recording(i.Start, TimeSpan.FromSeconds(Math.Max(0, i.Duration))))
            .OrderByDescending(r => r.Start)
            .ToList();
    }

    /// <summary>
    /// Opens a recording as one MP4 file. The caller owns the response and streams its content; the body is
    /// not buffered, so long recordings start downloading immediately.
    /// </summary>
    public async Task<HttpResponseMessage> OpenAsync(StreamDefinition stream, Recording recording, CancellationToken ct = default)
    {
        using var req = Request(stream, $"get?path={Uri.EscapeDataString(stream.Name)}&{RecordingQueryForPlayback(recording)}&format=mp4");
        var res = await httpFactory.CreateClient(HttpClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            using (res)
                await EnsureSuccess(res, ct);
        }
        return res;
    }

    /// <summary>Deletes every segment (file) that belongs to the recording.</summary>
    public async Task<int> DeleteAsync(StreamDefinition stream, Recording recording, CancellationToken ct = default)
    {
        var mtx = clients.Create(stream.Server!);
        var segments = await mtx.ListRecordingSegmentsAsync(stream.Name, ct);
        // The first segment can start slightly before the listed start; one second of slack covers that.
        var from = recording.Start - TimeSpan.FromSeconds(1);
        var deleted = 0;
        foreach (var start in segments.Where(s => s >= from && s < recording.End))
        {
            await mtx.DeleteRecordingSegmentAsync(stream.Name, start, ct);
            deleted++;
        }
        logger.LogInformation("Deleted recording of {Path} on {Server} from {Start:u} ({Count} segment(s))",
            stream.Name, stream.Server!.Name, recording.Start, deleted);
        return deleted;
    }

    /// <summary>File name for a download, e.g. "events-main_2026-10-19_20-15-00.mp4" (UTC).</summary>
    public static string FileName(StreamDefinition stream, Recording recording) =>
        $"{stream.Name.Replace('/', '-')}_{recording.Start.UtcDateTime:yyyy-MM-dd_HH-mm-ss}.mp4";

    // A Go duration ("…s") is accepted by every MediaMTX version; newer ones also take plain seconds.
    private static string RecordingQueryForPlayback(Recording r) => r.Query + "s";

    private static HttpRequestMessage Request(StreamDefinition stream, string pathAndQuery)
    {
        var server = stream.Server ?? throw new InvalidOperationException("Load the stream together with its server.");
        var req = new HttpRequestMessage(HttpMethod.Get, $"{server.PlaybackBaseUrl()}/{pathAndQuery}");
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{stream.ReadUser}:{stream.ReadPassword}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        return req;
    }

    private static async Task EnsureSuccess(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
            return;
        string? message = null;
        try
        {
            message = (await res.Content.ReadFromJsonAsync<MtxError>(ct))?.Error;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException) { }
        throw new MediaMtxException(res.StatusCode, message ?? res.ReasonPhrase ?? "unknown error");
    }

    private record PlaybackListEntry(DateTimeOffset Start, double Duration);
}
