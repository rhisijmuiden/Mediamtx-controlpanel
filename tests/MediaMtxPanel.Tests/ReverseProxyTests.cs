using System.Net;
using System.Net.Sockets;
using System.Text;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services;
using MediaMtxPanel.Services.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaMtxPanel.Tests;

public class ReverseProxyTests
{
    private static async Task<MediaServer> UpdateServerAsync(PanelFactory factory, Action<MediaServer> change)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var server = await db.Servers.FirstAsync();
        change(server);
        await db.SaveChangesAsync();
        factory.Services.GetRequiredService<MediaMtxPanel.Endpoints.MediaProxyServers>().Invalidate();
        return server;
    }

    private static StreamDefinition Stream(MediaServer server) =>
        new() { Name = "cam", Server = server, ServerId = server.Id, ReadUser = "viewer", ReadPassword = "secret" };

    [Fact]
    public async Task Without_reverse_proxy_players_use_the_mediamtx_ports()
    {
        using var factory = new PanelFactory();
        var server = await UpdateServerAsync(factory, _ => { });
        var player = factory.Services.GetRequiredService<StreamEndpointBuilder>().Player(Stream(server), "u", "p");

        Assert.Equal("http://stream.test:8889/cam/whep", player.WhepUrl);
        Assert.Equal("http://stream.test:8888/cam/index.m3u8", player.HlsUrl);
    }

    [Fact]
    public async Task Behind_reverse_proxy_players_use_the_public_https_address_without_ports()
    {
        using var factory = new PanelFactory();
        var server = await UpdateServerAsync(factory, _ => { });
        var store = factory.Services.GetRequiredService<PanelSettingsStore>();
        store.Save(store.Current with { ReverseProxy = new() { Enabled = true, PublicUrl = "https://watch.example.com/" } });

        var endpoints = factory.Services.GetRequiredService<StreamEndpointBuilder>();
        var player = endpoints.Player(Stream(server), "u", "p");

        Assert.Equal($"https://watch.example.com/media/{server.Id}/webrtc/cam/reader.js", player.ReaderJsUrl);
        Assert.Equal($"https://watch.example.com/media/{server.Id}/webrtc/cam/whep", player.WhepUrl);
        Assert.Equal($"https://watch.example.com/media/{server.Id}/hls/cam/index.m3u8", player.HlsUrl);
        Assert.Contains(endpoints.Ticket(Stream(server), "tok"),
            e => e.Url == $"https://ticket:tok@watch.example.com/media/{server.Id}/hls/cam/index.m3u8");
        // RTSP cannot go through an HTTP proxy and keeps its port.
        Assert.Contains(endpoints.Read(Stream(server)), e => e.Url.StartsWith("rtsp://viewer:secret@stream.test:8554/"));
    }

    [Fact]
    public async Task Custom_urls_and_direct_route_override_the_global_switch()
    {
        using var factory = new PanelFactory();
        var store = factory.Services.GetRequiredService<PanelSettingsStore>();
        store.Save(store.Current with { ReverseProxy = new() { Enabled = true, PublicUrl = "https://watch.example.com" } });
        var endpoints = factory.Services.GetRequiredService<StreamEndpointBuilder>();

        var custom = await UpdateServerAsync(factory, s =>
        {
            s.ViewerRoute = ViewerRoute.Custom;
            s.PublicHlsUrl = "https://hls.example.com/live";
            s.PublicWebRtcUrl = "https://rtc.example.com";
        });
        Assert.Equal("https://hls.example.com/live", endpoints.HlsBase(custom));
        Assert.Equal("https://rtc.example.com", endpoints.WebRtcBase(custom));

        var direct = await UpdateServerAsync(factory, s => s.ViewerRoute = ViewerRoute.Direct);
        Assert.Equal("http://stream.test:8888", endpoints.HlsBase(direct));
    }

    [Fact]
    public async Task The_panel_forwards_webrtc_signalling_and_rewrites_the_session_location()
    {
        using var mediamtx = new FakeMediaMtx(req => req.Path == "/cam/whep"
            ? "HTTP/1.1 201 Created\r\nLocation: /cam/whep/abc\r\nContent-Type: application/sdp\r\nContent-Length: 6\r\n\r\nanswer"
            : "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n");
        using var factory = new PanelFactory();
        var server = await UpdateServerAsync(factory, s =>
        {
            s.ApiUrl = "http://127.0.0.1:9997";
            s.WebRtcPort = mediamtx.Port;
            s.ViewerRoute = ViewerRoute.Panel;
        });
        var client = factory.Client();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/media/{server.Id}/webrtc/cam/whep")
        {
            Content = new StringContent("offer", Encoding.UTF8, "application/sdp"),
        };
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("viewer:secret")));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/media/{server.Id}/webrtc/cam/whep/abc", response.Headers.Location?.OriginalString);
        Assert.Equal("answer", await response.Content.ReadAsStringAsync());
        var seen = Assert.Single(mediamtx.Requests);
        Assert.Equal("POST", seen.Method);
        Assert.Contains("Authorization: Basic", seen.Head);

        // MediaMTX's own 401 reaches the player as it is, not as the panel's not-found page.
        var denied = await client.GetAsync($"/media/{server.Id}/webrtc/other/reader.js");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("", await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Servers_that_do_not_route_through_the_panel_are_not_forwarded()
    {
        using var factory = new PanelFactory();
        var server = await UpdateServerAsync(factory, s => s.ViewerRoute = ViewerRoute.Direct);

        var response = await factory.Client().GetAsync($"/media/{server.Id}/hls/cam/index.m3u8");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Setup_trusts_the_panel_as_proxy_when_the_hook_url_has_an_ip()
    {
        var yaml = MediaMtxConfigEditor.ConfigureForPanel("hlsTrustedProxies: []\n", "http://10.0.0.5:8080/api/mediamtx/auth?secret=x", 9997, null);

        Assert.Equal("[10.0.0.5]", MediaMtxConfigEditor.Get(yaml, "hlsTrustedProxies"));
        Assert.Equal("[10.0.0.5]", MediaMtxConfigEditor.Get(yaml, "webrtcTrustedProxies"));
        Assert.Null(MediaMtxConfigEditor.Get(MediaMtxConfigEditor.ConfigureForPanel("", "http://panel:8080/x", 9997, null), "hlsTrustedProxies"));
    }

    /// <summary>A minimal HTTP/1.1 server standing in for the MediaMTX WebRTC/HLS listener: one answer per connection.</summary>
    private sealed class FakeMediaMtx : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();

        public FakeMediaMtx(Func<(string Method, string Path, string Head), string> respond)
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream);
                    var first = head.Split("\r\n")[0].Split(' ');
                    var length = head.Split("\r\n").Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        .Select(l => int.Parse(l[15..].Trim())).FirstOrDefault();
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, stop.Token);
                    var request = (first[0], first[1], head);
                    Requests.Add(request);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(respond(request).Replace("\r\n\r\n", "\r\nConnection: close\r\n\r\n")), stop.Token);
                }
            });
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public List<(string Method, string Path, string Head)> Requests { get; } = [];

        private static async Task<string> ReadHeadAsync(NetworkStream stream)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
            {
                if (await stream.ReadAsync(one) == 0)
                    break;
                bytes.Add(one[0]);
            }
            return Encoding.ASCII.GetString([.. bytes]);
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }
}
