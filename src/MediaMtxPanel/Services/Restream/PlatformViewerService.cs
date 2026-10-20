using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using MediaMtxPanel.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Services.Restream;

/// <summary>Latest viewer count of a restream target on its platform.</summary>
public record PlatformViewers(int? Count, string? Error, DateTime CheckedAt);

/// <summary>
/// Asks YouTube, Twitch and Facebook how many people watch a restream target, for targets that are
/// live and have viewer-count credentials. Polls every 30 seconds (YouTube every 60 seconds, to stay
/// within the daily API quota).
/// </summary>
public class PlatformViewerService(
    IServiceProvider services,
    IHttpClientFactory httpFactory,
    IDataProtectionProvider dataProtection,
    RestreamManager manager,
    ILogger<PlatformViewerService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    // A YouTube search costs 100 of the 10,000 daily quota units; a video lookup costs 1.
    private static readonly TimeSpan YouTubeInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan YouTubeSearchInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<int, PlatformViewers> readings = new();
    private readonly ConcurrentDictionary<int, int> streamOfTarget = new();
    private readonly ConcurrentDictionary<int, (string? VideoId, DateTime SearchedAt)> youTubeVideos = new();
    private readonly ConcurrentDictionary<string, (string Token, DateTime Expires)> twitchTokens = new();

    public PlatformViewers? Get(int targetId) => readings.GetValueOrDefault(targetId);

    /// <summary>Sum of the latest known platform viewer counts of the stream's live targets.</summary>
    public int GetTotal(int streamId) =>
        readings.Where(r => streamOfTarget.GetValueOrDefault(r.Key) == streamId).Sum(r => r.Value.Count ?? 0);

    public static bool Supports(RestreamPlatform p) => p is RestreamPlatform.YouTube or RestreamPlatform.Twitch or RestreamPlatform.Facebook;

    public static bool IsConfigured(RestreamTarget t) =>
        Supports(t.Platform) && !string.IsNullOrWhiteSpace(t.ViewerChannel) && !string.IsNullOrEmpty(t.ViewerSecretProtected)
        && (t.Platform != RestreamPlatform.Twitch || !string.IsNullOrWhiteSpace(t.ViewerClientId));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        do
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reading platform viewer counts failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        List<RestreamTarget> targets;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            targets = await db.RestreamTargets.AsNoTracking().Where(t => t.Enabled).ToListAsync(ct);

        var live = targets.Where(t => IsConfigured(t) && manager.GetStatus(t).State == RestreamState.Live).ToList();
        var liveIds = live.Select(t => t.Id).ToHashSet();
        foreach (var id in readings.Keys.Where(id => !liveIds.Contains(id)))
        {
            readings.TryRemove(id, out _);
            streamOfTarget.TryRemove(id, out _);
            youTubeVideos.TryRemove(id, out _);
        }

        await Task.WhenAll(live.Select(async t =>
        {
            if (t.Platform == RestreamPlatform.YouTube && readings.TryGetValue(t.Id, out var last)
                && DateTime.UtcNow - last.CheckedAt < YouTubeInterval - TimeSpan.FromSeconds(5))
                return;
            streamOfTarget[t.Id] = t.StreamId;
            readings[t.Id] = await ReadAsync(t, ct);
        }));
    }

    internal async Task<PlatformViewers> ReadAsync(RestreamTarget t, CancellationToken ct)
    {
        try
        {
            var secret = dataProtection.CreateProtector(RestreamService.ProtectorPurpose).Unprotect(t.ViewerSecretProtected!);
            var channel = t.ViewerChannel!.Trim();
            var count = t.Platform switch
            {
                RestreamPlatform.YouTube => await YouTubeAsync(t.Id, channel, secret, ct),
                RestreamPlatform.Twitch => await TwitchAsync(t.ViewerClientId!.Trim(), secret, channel, ct),
                _ => await FacebookAsync(channel, secret, ct),
            };
            return new(count, null, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or PlatformApiException or TaskCanceledException
                                       or System.Security.Cryptography.CryptographicException && !ct.IsCancellationRequested)
        {
            logger.LogInformation("Viewer count for restream target {Id} ({Platform}) failed: {Message}", t.Id, t.Platform, ex.Message);
            return new(null, ex is PlatformApiException ? ex.Message : "The platform could not be reached.", DateTime.UtcNow);
        }
    }

    /// <summary>The channel's current live video (found with a search, then cached), and its concurrent viewers.</summary>
    private async Task<int?> YouTubeAsync(int targetId, string channelId, string apiKey, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("platforms");
        var cached = youTubeVideos.GetValueOrDefault(targetId);
        if (cached.VideoId is null)
        {
            if (DateTime.UtcNow - cached.SearchedAt < YouTubeSearchInterval)
                return null;
            using var search = await GetJsonAsync(http, $"https://www.googleapis.com/youtube/v3/search?part=id&type=video&eventType=live&maxResults=1&channelId={Uri.EscapeDataString(channelId)}&key={Uri.EscapeDataString(apiKey)}", ct);
            var items = search.RootElement.GetProperty("items");
            var videoId = items.GetArrayLength() > 0 ? items[0].GetProperty("id").GetProperty("videoId").GetString() : null;
            youTubeVideos[targetId] = (videoId, DateTime.UtcNow);
            if (videoId is null)
                return null;
            cached = (videoId, DateTime.UtcNow);
        }

        using var video = await GetJsonAsync(http, $"https://www.googleapis.com/youtube/v3/videos?part=liveStreamingDetails&id={Uri.EscapeDataString(cached.VideoId!)}&key={Uri.EscapeDataString(apiKey)}", ct);
        var list = video.RootElement.GetProperty("items");
        if (list.GetArrayLength() > 0 && list[0].TryGetProperty("liveStreamingDetails", out var details)
            && details.TryGetProperty("concurrentViewers", out var viewers) && int.TryParse(viewers.GetString(), out var n))
            return n;
        // The broadcast ended (or has no viewer count): look for a new live video next time.
        youTubeVideos[targetId] = (null, DateTime.MinValue);
        return null;
    }

    private async Task<int?> TwitchAsync(string clientId, string clientSecret, string login, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("platforms");
        if (!twitchTokens.TryGetValue(clientId, out var token) || token.Expires < DateTime.UtcNow.AddMinutes(5))
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "client_credentials",
            });
            using var res = await http.PostAsync("https://id.twitch.tv/oauth2/token", form, ct);
            using var json = await ReadJsonAsync(res, ct);
            token = (json.RootElement.GetProperty("access_token").GetString()!,
                DateTime.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32()));
            twitchTokens[clientId] = token;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_login={Uri.EscapeDataString(login.ToLowerInvariant())}");
        req.Headers.Add("Client-Id", clientId);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var streams = await ReadJsonAsync(await http.SendAsync(req, ct), ct);
        var data = streams.RootElement.GetProperty("data");
        return data.GetArrayLength() > 0 ? data[0].GetProperty("viewer_count").GetInt32() : null;
    }

    private async Task<int?> FacebookAsync(string pageId, string pageToken, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("platforms");
        var url = $"https://graph.facebook.com/v23.0/{Uri.EscapeDataString(pageId)}/live_videos?broadcast_status=%5B%22LIVE%22%5D&fields=live_views&limit=1&access_token={Uri.EscapeDataString(pageToken)}";
        using var json = await GetJsonAsync(http, url, ct);
        var data = json.RootElement.GetProperty("data");
        return data.GetArrayLength() > 0 && data[0].TryGetProperty("live_views", out var v) ? v.GetInt32() : null;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken ct) =>
        await ReadJsonAsync(await http.GetAsync(url, ct), ct);

    /// <summary>Reads a JSON answer; an error answer becomes a <see cref="PlatformApiException"/> with the platform's own message.</summary>
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage res, CancellationToken ct)
    {
        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
                return JsonDocument.Parse(body);
            string? message = null;
            try
            {
                using var err = JsonDocument.Parse(body);
                var root = err.RootElement;
                // Google: {"error":{"message":…}}, Facebook: {"error":{"message":…}}, Twitch: {"message":…}
                if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m))
                    message = m.GetString();
                else if (root.TryGetProperty("message", out var m2))
                    message = m2.GetString();
            }
            catch (JsonException) { }
            throw new PlatformApiException($"{(int)res.StatusCode}: {message ?? res.ReasonPhrase}");
        }
    }

    private sealed class PlatformApiException(string message) : Exception(message);
}
