using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaMtxPanel.Data;

namespace MediaMtxPanel.Services;

/// <summary>Thin client for the Control API (v3) of one MediaMTX server. Create through <see cref="MediaMtxClientFactory"/>.</summary>
public class MediaMtxClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public MediaMtxClient(HttpClient http, MediaServer server)
    {
        _http = http;
        _http.BaseAddress = new Uri(server.ApiUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrEmpty(server.ApiUser))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{server.ApiUser}:{server.ApiPassword}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    public Task<MtxInfo?> GetInfoAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<MtxInfo>("v3/info", Json, ct);

    public async Task<List<MtxPath>> ListPathsAsync(CancellationToken ct = default)
    {
        var list = await _http.GetFromJsonAsync<MtxList<MtxPath>>("v3/paths/list?itemsPerPage=1000", Json, ct);
        return list?.Items ?? [];
    }

    public async Task<MtxPath?> GetPathAsync(string name, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync($"v3/paths/get/{Escape(name)}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccess(res, ct);
        return await res.Content.ReadFromJsonAsync<MtxPath>(Json, ct);
    }

    public async Task<List<MtxPathConfig>> ListPathConfigsAsync(CancellationToken ct = default)
    {
        var list = await _http.GetFromJsonAsync<MtxList<MtxPathConfig>>("v3/config/paths/list?itemsPerPage=1000", Json, ct);
        return list?.Items ?? [];
    }

    public async Task<MtxPathConfig?> GetPathConfigAsync(string name, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync($"v3/config/paths/get/{Escape(name)}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccess(res, ct);
        return await res.Content.ReadFromJsonAsync<MtxPathConfig>(Json, ct);
    }

    public async Task AddPathConfigAsync(string name, MtxPathConfig config, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync($"v3/config/paths/add/{Escape(name)}", config, Json, ct);
        await EnsureSuccess(res, ct);
    }

    public async Task ReplacePathConfigAsync(string name, MtxPathConfig config, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync($"v3/config/paths/replace/{Escape(name)}", config, Json, ct);
        await EnsureSuccess(res, ct);
    }

    public async Task DeletePathConfigAsync(string name, CancellationToken ct = default)
    {
        using var res = await _http.DeleteAsync($"v3/config/paths/delete/{Escape(name)}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
            return;
        await EnsureSuccess(res, ct);
    }

    /// <summary>Start times of the recording segments (files) of a path; empty when there are none.</summary>
    public async Task<List<DateTimeOffset>> ListRecordingSegmentsAsync(string name, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync($"v3/recordings/get/{Escape(name)}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
            return [];
        await EnsureSuccess(res, ct);
        var rec = await res.Content.ReadFromJsonAsync<MtxRecording>(Json, ct);
        return rec?.Segments.Select(s => s.Start).ToList() ?? [];
    }

    /// <summary>Deletes one recording segment (file), identified by its exact start time.</summary>
    public async Task DeleteRecordingSegmentAsync(string name, DateTimeOffset start, CancellationToken ct = default)
    {
        var query = $"path={Uri.EscapeDataString(name)}&start={Uri.EscapeDataString(RecordingTime(start))}";
        var res = await _http.DeleteAsync($"v3/recordings/segments/delete?{query}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            // Older MediaMTX versions only know the deprecated name of this endpoint.
            res.Dispose();
            res = await _http.DeleteAsync($"v3/recordings/deletesegment?{query}", ct);
        }
        using (res)
            await EnsureSuccess(res, ct);
    }

    /// <summary>RFC 3339 with all the precision MediaMTX needs to find a segment by its start.</summary>
    public static string RecordingTime(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Disconnects a publisher or reader, using the type reported in /v3/paths/list.</summary>
    public async Task KickAsync(string type, string id, CancellationToken ct = default)
    {
        var endpoint = type switch
        {
            "rtspSession" => "rtspsessions",
            "rtspsSession" => "rtspssessions",
            "rtmpConn" => "rtmpconns",
            "rtmpsConn" => "rtmpsconns",
            "srtConn" => "srtconns",
            "webRTCSession" => "webrtcsessions",
            "hlsSession" => "hlssessions",
            "moqSession" => "moqsessions",
            _ => throw new NotSupportedException($"Connections of type '{type}' cannot be disconnected."),
        };
        using var res = await _http.PostAsync($"v3/{endpoint}/kick/{Uri.EscapeDataString(id)}", null, ct);
        await EnsureSuccess(res, ct);
    }

    public static bool CanKick(string? type) => type is "rtspSession" or "rtspsSession" or "rtmpConn" or "rtmpsConn"
        or "srtConn" or "webRTCSession" or "hlsSession" or "moqSession";

    // Path names may contain slashes, which MediaMTX expects unescaped.
    private static string Escape(string name) => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

    private static async Task EnsureSuccess(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
            return;
        string? message = null;
        try
        {
            var err = await res.Content.ReadFromJsonAsync<MtxError>(Json, ct);
            message = err?.Error;
        }
        catch (JsonException) { }
        catch (NotSupportedException) { }
        throw new MediaMtxException(res.StatusCode, message ?? res.ReasonPhrase ?? "unknown error");
    }
}

public class MediaMtxException(HttpStatusCode status, string message)
    : Exception($"MediaMTX API returned {(int)status}: {message}")
{
    public HttpStatusCode Status { get; } = status;
}

public record MtxError(string? Status, string? Error);

public record MtxList<T>(int ItemCount, int PageCount, List<T> Items);

public record MtxInfo(string Version, DateTime Started);

public class MtxPath
{
    public string Name { get; set; } = "";
    public string? ConfName { get; set; }
    public bool Ready { get; set; }
    public bool Available { get; set; }
    public bool Online { get; set; }
    public DateTime? ReadyTime { get; set; }
    public DateTime? OnlineTime { get; set; }
    public MtxConnRef? Source { get; set; }
    public List<MtxTrack>? Tracks2 { get; set; }
    public List<string>? Tracks { get; set; }
    public List<MtxConnRef> Readers { get; set; } = [];
    public ulong InboundBytes { get; set; }
    public ulong OutboundBytes { get; set; }
    public ulong BytesReceived { get; set; }
    public ulong BytesSent { get; set; }

    public bool IsLive => Online || Ready || Available;

    public IEnumerable<string> Codecs =>
        Tracks2 is { Count: > 0 } ? Tracks2.Select(t => t.Codec ?? "?") : Tracks ?? [];

    public ulong Received => InboundBytes > 0 ? InboundBytes : BytesReceived;
    public ulong Sent => OutboundBytes > 0 ? OutboundBytes : BytesSent;
}

public record MtxConnRef(string Type, string Id);

public record MtxRecording(string Name, List<MtxRecordingSegment> Segments);

public record MtxRecordingSegment(DateTimeOffset Start);

public class MtxTrack
{
    public string? Codec { get; set; }
}

/// <summary>Subset of a MediaMTX path configuration that the panel manages.</summary>
public class MtxPathConfig
{
    public string? Name { get; set; }
    public string? Source { get; set; }
    public bool? SourceOnDemand { get; set; }
    public bool? Record { get; set; }
    public string? RecordDeleteAfter { get; set; }
    public int? MaxReaders { get; set; }
}

/// <summary>Durations as MediaMTX writes them in its configuration: Go durations with an optional day part, e.g. "7d" or "1d2h0m0s".</summary>
public static partial class MtxDuration
{
    public static TimeSpan? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var match = Pattern().Match(value.Trim());
        if (!match.Success)
            return null;
        var total = TimeSpan.Zero;
        foreach (System.Text.RegularExpressions.Capture part in match.Groups["part"].Captures)
        {
            var unit = part.Value.TrimStart("0123456789.".ToCharArray());
            var number = double.Parse(part.Value[..^unit.Length], System.Globalization.CultureInfo.InvariantCulture);
            total += unit switch
            {
                "d" => TimeSpan.FromDays(number),
                "h" => TimeSpan.FromHours(number),
                "m" => TimeSpan.FromMinutes(number),
                "s" => TimeSpan.FromSeconds(number),
                "ms" => TimeSpan.FromMilliseconds(number),
                "us" or "µs" => TimeSpan.FromMicroseconds(number),
                _ => TimeSpan.FromTicks((long)(number / 100)),
            };
        }
        return total;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:(?<part>[0-9.]+(?:d|h|ms|m|s|us|µs|ns)))+$")]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}

public class MediaMtxClientFactory(IHttpClientFactory httpClientFactory)
{
    public MediaMtxClient Create(MediaServer server) =>
        new(httpClientFactory.CreateClient("mediamtx"), server);
}
