using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MediaMtxPanel.Tests;

public class HealthTests
{
    [Fact]
    public async Task Healthz_answers_anonymously()
    {
        using var factory = new PanelFactory();
        var response = await factory.Client().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal("mediamtx-panel", json.GetProperty("app").GetString());
        Assert.Equal(1, json.GetProperty("contract").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Without_a_path_base_the_pages_use_the_root()
    {
        using var factory = new PanelFactory();
        var html = await factory.Client().GetStringAsync("/Account/Login");
        Assert.Contains("<base href=\"/\" />", html);
    }
}

public class PathBaseTests
{
    private static PanelFactory Factory() => new(new Dictionary<string, string?> { ["Panel:PathBase"] = "/stream/" });

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/stream/healthz")]
    public async Task Healthz_answers_at_the_root_and_under_the_base_path(string path)
    {
        using var factory = Factory();
        var response = await factory.Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Base_path_without_slash_redirects()
    {
        using var factory = Factory();
        var response = await factory.Client().GetAsync("/stream?x=1");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/stream/?x=1", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Pages_redirects_and_cookies_live_under_the_base_path()
    {
        using var factory = Factory();
        var client = factory.Client();

        var redirect = await client.GetAsync("/stream/streams");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.StartsWith("/stream/Account/Login", redirect.Headers.Location?.PathAndQuery ?? redirect.Headers.Location?.OriginalString);

        var login = await client.GetAsync("/stream/Account/Login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("<base href=\"/stream/\" />", await login.Content.ReadAsStringAsync());
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.Contains("path=/stream", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Auth_hook_stays_reachable_at_the_root()
    {
        using var factory = Factory();
        var response = await factory.Client().PostAsJsonAsync("/api/mediamtx/auth?secret=wrong", new { action = "read", path = "x" });
        // Reached the hook: an unknown secret is refused by the hook itself, not a 404.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Api_works_under_the_base_path()
    {
        using var factory = new PanelFactory(new Dictionary<string, string?>
        {
            ["Panel:PathBase"] = "/stream",
            ["Panel:ServiceToken"] = PanelFactory.Token,
        });
        var created = await factory.TokenClient().PostAsJsonAsync("/stream/api/v1/streams", new { name = "based", publicRead = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.StartsWith("/stream/api/v1/streams/", created.Headers.Location?.OriginalString);
    }
}

public class ServiceTokenTests
{
    private static PanelFactory WithToken() => new(new Dictionary<string, string?> { ["Panel:ServiceToken"] = PanelFactory.Token });

    [Fact]
    public async Task Good_token_is_an_administrator_on_the_api()
    {
        using var factory = WithToken();
        var response = await factory.TokenClient().GetAsync("/api/v1/streams");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-token-0123456789abcdefghijklmnopqrstuvw")]
    [InlineData("short")]
    public async Task Wrong_token_gets_401(string token)
    {
        using var factory = WithToken();
        var response = await factory.TokenClient(token).GetAsync("/api/v1/streams");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task No_token_gets_401()
    {
        using var factory = WithToken();
        var response = await factory.Client().GetAsync("/api/v1/streams");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_configured_token_bearer_headers_are_ignored()
    {
        using var factory = new PanelFactory();
        var response = await factory.TokenClient().GetAsync("/api/v1/streams");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Too_short_configured_token_is_ignored()
    {
        using var factory = new PanelFactory(new Dictionary<string, string?> { ["Panel:ServiceToken"] = "short-token" });
        var response = await factory.TokenClient("short-token").GetAsync("/api/v1/streams");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_does_not_log_in_to_the_pages()
    {
        using var factory = WithToken();
        var response = await factory.TokenClient().GetAsync("/streams");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Account/Login", response.Headers.Location?.OriginalString);
    }
}

public class StreamsApiTests
{
    [Fact]
    public async Task Create_get_list_and_delete()
    {
        using var factory = new PanelFactory(new Dictionary<string, string?> { ["Panel:ServiceToken"] = PanelFactory.Token });
        var client = factory.TokenClient();

        var created = await client.PostAsJsonAsync("/api/v1/streams", new { name = "channel1", publicRead = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var stream = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = stream.GetProperty("id").GetInt32();
        Assert.Equal("channel1", stream.GetProperty("name").GetString());
        Assert.False(stream.GetProperty("publicRead").GetBoolean());
        Assert.Equal($"/api/v1/streams/{id}", created.Headers.Location?.OriginalString);
        // No MediaMTX listens in the test, so the push reports a warning; the stream is stored anyway.
        Assert.True(stream.TryGetProperty("warning", out _));

        var publish = stream.GetProperty("publish");
        Assert.Matches(@"^rtmp://stream\.test:1935/channel1\?user=pub_\w+&pass=\w+$", publish.GetProperty("rtmp").GetString());
        Assert.Matches(@"^srt://stream\.test:8890\?streamid=publish:channel1:pub_\w+:\w+$", publish.GetProperty("srt").GetString());
        Assert.Matches(@"^rtsp://pub_\w+:\w+@stream\.test:8554/channel1$", publish.GetProperty("rtsp").GetString());
        var play = stream.GetProperty("play");
        Assert.Matches(@"^http://view_\w+:\w+@stream\.test:8888/channel1/index\.m3u8$", play.GetProperty("hls").GetString());
        Assert.Matches(@"^http://view_\w+:\w+@stream\.test:8889/channel1/$", play.GetProperty("webrtc").GetString());
        Assert.Matches(@"^rtsp://view_\w+:\w+@stream\.test:8554/channel1$", play.GetProperty("rtsp").GetString());
        Assert.Matches(@"^srt://stream\.test:8890\?streamid=read:channel1:view_\w+:\w+$", play.GetProperty("srt").GetString());

        var one = await client.GetFromJsonAsync<JsonElement>($"/api/v1/streams/{id}");
        Assert.Equal("channel1", one.GetProperty("name").GetString());
        Assert.False(one.TryGetProperty("warning", out _));

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/streams");
        Assert.Contains(list.EnumerateArray(), s => s.GetProperty("id").GetInt32() == id);

        var duplicate = await client.PostAsJsonAsync("/api/v1/streams", new { name = "channel1", publicRead = true });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/streams/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/streams/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/streams/{id}")).StatusCode);
    }

    [Fact]
    public async Task Public_stream_has_read_urls_without_credentials()
    {
        using var factory = new PanelFactory(new Dictionary<string, string?> { ["Panel:ServiceToken"] = PanelFactory.Token });
        var created = await factory.TokenClient().PostAsJsonAsync("/api/v1/streams", new { name = "open", publicRead = true });
        var play = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("play");
        Assert.Equal("http://stream.test:8888/open/index.m3u8", play.GetProperty("hls").GetString());
        Assert.Equal("http://stream.test:8889/open/", play.GetProperty("webrtc").GetString());
        Assert.Equal("rtsp://stream.test:8554/open", play.GetProperty("rtsp").GetString());
        Assert.Equal("srt://stream.test:8890?streamid=read:open", play.GetProperty("srt").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("-bad")]
    [InlineData("a/../b")]
    public async Task Invalid_names_get_400(string name)
    {
        using var factory = new PanelFactory(new Dictionary<string, string?> { ["Panel:ServiceToken"] = PanelFactory.Token });
        var response = await factory.TokenClient().PostAsJsonAsync("/api/v1/streams", new { name, publicRead = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public class TrustedProxyTests
{
    [Fact]
    public async Task Unlisted_proxy_cannot_change_the_scheme()
    {
        using var factory = new PanelFactory(new Dictionary<string, string?> { ["Panel:TrustedProxies"] = "10.9.9.9, 10.1.0.0/16" });
        var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");
        var response = await client.GetAsync("/streams");
        // The redirect to the login page keeps http: the forwarded headers were not believed.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://", response.Headers.Location?.ToString());
    }
}
