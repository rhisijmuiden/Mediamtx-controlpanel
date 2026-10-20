using System.Net;
using System.Text;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services.Restream;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaMtxPanel.Tests;

public class PlatformViewerTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static (PlatformViewerService, FakeHandler) Create(Func<HttpRequestMessage, (HttpStatusCode, string)> answer)
    {
        var handler = new FakeHandler(answer);
        return (new PlatformViewerService(null!, new Factory(handler), Protection, null!, NullLogger<PlatformViewerService>.Instance), handler);
    }

    private static RestreamTarget Target(RestreamPlatform platform, string channel, string secret, string? clientId = null) => new()
    {
        Id = 1,
        Platform = platform,
        ViewerChannel = channel,
        ViewerClientId = clientId,
        ViewerSecretProtected = Protection.CreateProtector(RestreamService.ProtectorPurpose).Protect(secret),
    };

    [Fact]
    public async Task YouTube_finds_the_live_video_once_and_reads_its_viewers()
    {
        var (service, handler) = Create(req => req.RequestUri!.AbsolutePath.EndsWith("/search")
            ? (HttpStatusCode.OK, """{"items":[{"id":{"kind":"youtube#video","videoId":"abc123"}}]}""")
            : (HttpStatusCode.OK, """{"items":[{"liveStreamingDetails":{"concurrentViewers":"1234"}}]}"""));
        var target = Target(RestreamPlatform.YouTube, "UCxyz", "api-key");

        Assert.Equal(1234, (await service.ReadAsync(target, default)).Count);
        Assert.Equal(1234, (await service.ReadAsync(target, default)).Count);

        Assert.Single(handler.Requests, r => r.Contains("/search") && r.Contains("channelId=UCxyz") && r.Contains("eventType=live"));
        Assert.Equal(2, handler.Requests.Count(r => r.Contains("/videos") && r.Contains("id=abc123")));
    }

    [Fact]
    public async Task YouTube_without_a_live_video_reports_no_count()
    {
        var (service, _) = Create(_ => (HttpStatusCode.OK, """{"items":[]}"""));
        var reading = await service.ReadAsync(Target(RestreamPlatform.YouTube, "UCxyz", "api-key"), default);
        Assert.Null(reading.Count);
        Assert.Null(reading.Error);
    }

    [Fact]
    public async Task Twitch_gets_an_app_token_and_reads_the_stream()
    {
        var (service, handler) = Create(req => req.RequestUri!.Host == "id.twitch.tv"
            ? (HttpStatusCode.OK, """{"access_token":"tok","expires_in":3600,"token_type":"bearer"}""")
            : req.Headers.Authorization?.Parameter == "tok" && req.Headers.GetValues("Client-Id").Single() == "cid"
                ? (HttpStatusCode.OK, """{"data":[{"user_login":"mychannel","viewer_count":42}]}""")
                : (HttpStatusCode.Unauthorized, """{"message":"Invalid OAuth token"}"""));

        var reading = await service.ReadAsync(Target(RestreamPlatform.Twitch, "MyChannel", "secret", "cid"), default);

        Assert.Equal(42, reading.Count);
        Assert.Contains(handler.Requests, r => r.Contains("helix/streams?user_login=mychannel"));
    }

    [Fact]
    public async Task Facebook_reads_live_views_of_the_page_live_video()
    {
        var (service, handler) = Create(_ => (HttpStatusCode.OK, """{"data":[{"live_views":17,"id":"999"}]}"""));
        var reading = await service.ReadAsync(Target(RestreamPlatform.Facebook, "12345", "page-token"), default);
        Assert.Equal(17, reading.Count);
        Assert.Contains(handler.Requests, r => r.Contains("/12345/live_videos") && r.Contains("fields=live_views"));
    }

    [Fact]
    public async Task Platform_errors_are_reported_with_their_message()
    {
        var (service, _) = Create(_ => (HttpStatusCode.BadRequest, """{"error":{"message":"API key not valid. Please pass a valid API key.","code":400}}"""));
        var reading = await service.ReadAsync(Target(RestreamPlatform.YouTube, "UCxyz", "bad-key"), default);
        Assert.Null(reading.Count);
        Assert.Equal("400: API key not valid. Please pass a valid API key.", reading.Error);
    }

    [Fact]
    public void Viewer_count_needs_a_channel_and_a_secret_and_for_Twitch_a_client_id()
    {
        Assert.True(PlatformViewerService.IsConfigured(Target(RestreamPlatform.YouTube, "UCxyz", "k")));
        Assert.False(PlatformViewerService.IsConfigured(Target(RestreamPlatform.Twitch, "chan", "s")));
        Assert.True(PlatformViewerService.IsConfigured(Target(RestreamPlatform.Twitch, "chan", "s", "cid")));
        Assert.False(PlatformViewerService.IsConfigured(Target(RestreamPlatform.Custom, "x", "s")));
        Assert.False(PlatformViewerService.IsConfigured(Target(RestreamPlatform.Facebook, "", "s")));
    }
}
