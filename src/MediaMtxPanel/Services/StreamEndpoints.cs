using MediaMtxPanel.Data;
using MediaMtxPanel.Options;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Services;

public record StreamEndpoint(string Protocol, string Purpose, string Url, string? Note = null, bool HasCredentials = false);

public record ObsSettings(string Server, string StreamKey);

/// <summary>Options for wwwroot/js/player.js.</summary>
public record PlayerOptions(string Mode, string ReaderJsUrl, string WhepUrl, string HlsUrl, string User, string Pass);

/// <summary>Builds the public connection URLs for a stream, using the settings of the server it runs on.</summary>
public class StreamEndpointBuilder(PublicAddress address, IOptions<PanelOptions> options)
{
    /// <summary>Path under which the panel forwards HLS and WebRTC of a server; see Endpoints/MediaProxyEndpoints.cs.</summary>
    public const string ProxyPrefix = "/media";

    private static MediaServer Srv(StreamDefinition s) =>
        s.Server ?? throw new InvalidOperationException("Load the stream together with its server.");

    /// <summary>The route viewers take to this server, with <see cref="ViewerRoute.Auto"/> resolved.</summary>
    public ViewerRoute RouteOf(MediaServer server) => server.ViewerRoute switch
    {
        ViewerRoute.Auto => address.BehindReverseProxy ? ViewerRoute.Panel : ViewerRoute.Direct,
        ViewerRoute.Custom when string.IsNullOrWhiteSpace(server.PublicHlsUrl) && string.IsNullOrWhiteSpace(server.PublicWebRtcUrl) => ViewerRoute.Direct,
        var route => route,
    };

    /// <summary>Base URL of HLS on the server, without a trailing slash; the stream name follows.</summary>
    public string HlsBase(MediaServer server) => Base(server, "hls", server.HlsPort, server.PublicHlsUrl);

    /// <summary>Base URL of WebRTC (WHEP, WHIP, reader.js) on the server, without a trailing slash.</summary>
    public string WebRtcBase(MediaServer server) => Base(server, "webrtc", server.WebRtcPort, server.PublicWebRtcUrl);

    private string Base(MediaServer server, string kind, int port, string? custom)
    {
        var direct = $"{(server.HttpsUrls ? "https" : "http")}://{server.PublicHost}:{port}";
        return RouteOf(server) switch
        {
            ViewerRoute.Panel => $"{PanelBase(server)}{ProxyPrefix}/{server.Id}/{kind}",
            ViewerRoute.Custom => string.IsNullOrWhiteSpace(custom) ? direct : custom.Trim().TrimEnd('/'),
            _ => direct,
        };
    }

    // Without a configured address, assume the proxy serves the panel with https on the server's public host.
    private string PanelBase(MediaServer server) =>
        address.Configured ?? $"https://{server.PublicHost}{PanelOptions.NormalizePathBase(options.Value.PathBase)}";

    /// <summary>Puts <paramref name="userInfo"/> ("user:pass@", already escaped) after the scheme of <paramref name="url"/>.</summary>
    public static string WithUserInfo(string url, string userInfo)
    {
        var i = url.IndexOf("://", StringComparison.Ordinal);
        return userInfo.Length == 0 || i < 0 ? url : url[..(i + 3)] + userInfo + url[(i + 3)..];
    }

    public IReadOnlyList<StreamEndpoint> Publish(StreamDefinition s)
    {
        if (s.SourceType != StreamSourceType.Publisher)
            return [];

        var (u, p) = (Enc(s.PublishUser), Enc(s.PublishPassword));
        var h = Srv(s).PublicHost;
        return
        [
            new("RTSP", "Publish", $"rtsp://{u}:{p}@{h}:{Srv(s).RtspPort}/{s.Name}", HasCredentials: true),
            new("RTMP", "Publish", $"rtmp://{h}:{Srv(s).RtmpPort}/{s.Name}?user={u}&pass={p}", HasCredentials: true),
            new("SRT", "Publish", $"srt://{h}:{Srv(s).SrtPort}?streamid=publish:{s.Name}:{s.PublishUser}:{s.PublishPassword}&pkt_size=1316", HasCredentials: true),
            new("WebRTC (WHIP)", "Publish", $"{WebRtcBase(Srv(s))}/{s.Name}/whip",
                "Use the publish username and password as HTTP Basic authentication (or as bearer token user:pass)."),
            new("WebRTC (browser)", "Publish", $"{WebRtcBase(Srv(s))}/{s.Name}/publish",
                "Open in a browser to publish from a webcam or screen."),
        ];
    }

    public IReadOnlyList<StreamEndpoint> Read(StreamDefinition s)
    {
        var h = Srv(s).PublicHost;
        var creds = s.PublicRead ? "" : $"{Enc(s.ReadUser)}:{Enc(s.ReadPassword)}@";
        var rtmpQuery = s.PublicRead ? "" : $"?user={Enc(s.ReadUser)}&pass={Enc(s.ReadPassword)}";
        var srtCreds = s.PublicRead ? "" : $":{s.ReadUser}:{s.ReadPassword}";
        var httpNote = s.PublicRead ? null : "The browser asks for the viewer username and password.";
        return
        [
            new("RTSP", "Watch", $"rtsp://{creds}{h}:{Srv(s).RtspPort}/{s.Name}", HasCredentials: !s.PublicRead),
            new("RTMP", "Watch", $"rtmp://{h}:{Srv(s).RtmpPort}/{s.Name}{rtmpQuery}", HasCredentials: !s.PublicRead),
            new("SRT", "Watch", $"srt://{h}:{Srv(s).SrtPort}?streamid=read:{s.Name}{srtCreds}", HasCredentials: !s.PublicRead),
            new("WebRTC (WHEP)", "Watch", $"{WebRtcBase(Srv(s))}/{s.Name}/whep",
                s.PublicRead ? null : "Use the viewer credentials as HTTP Basic authentication."),
            new("WebRTC (browser)", "Watch", $"{WebRtcBase(Srv(s))}/{s.Name}/", httpNote),
            new("HLS", "Watch", $"{HlsBase(Srv(s))}/{s.Name}/index.m3u8", httpNote),
            new("HLS (browser)", "Watch", $"{HlsBase(Srv(s))}/{s.Name}/", httpNote),
        ];
    }

    public ObsSettings Obs(StreamDefinition s) =>
        new($"rtmp://{Srv(s).PublicHost}:{Srv(s).RtmpPort}",
            $"{s.Name}?user={Enc(s.PublishUser)}&pass={Enc(s.PublishPassword)}");

    public string FfmpegPublishExample(StreamDefinition s) =>
        $"ffmpeg -re -stream_loop -1 -i input.mp4 -c copy -f rtsp \"rtsp://{Enc(s.PublishUser)}:{Enc(s.PublishPassword)}@{Srv(s).PublicHost}:{Srv(s).RtspPort}/{s.Name}\"";

    public string FfplayExample(StreamDefinition s)
    {
        var creds = s.PublicRead ? "" : $"{Enc(s.ReadUser)}:{Enc(s.ReadPassword)}@";
        return $"ffplay \"rtsp://{creds}{Srv(s).PublicHost}:{Srv(s).RtspPort}/{s.Name}\"";
    }

    /// <summary>Browser player settings. Pass the credentials the viewer watches with (read creds or a ticket).</summary>
    public PlayerOptions Player(StreamDefinition s, string user, string pass, string mode = "webrtc")
    {
        var webrtc = $"{WebRtcBase(Srv(s))}/{s.Name}";
        return new PlayerOptions(mode, $"{webrtc}/reader.js", $"{webrtc}/whep",
            $"{HlsBase(Srv(s))}/{s.Name}/index.m3u8", user, pass);
    }

    /// <summary>URLs a paying viewer can open in VLC or another player, with the ticket as credentials.</summary>
    public IReadOnlyList<StreamEndpoint> Ticket(StreamDefinition s, string token)
    {
        var h = Srv(s).PublicHost;
        var t = Enc(token);
        return
        [
            new("HLS (VLC, smart TV, mobile)", "Watch", WithUserInfo($"{HlsBase(Srv(s))}/{s.Name}/index.m3u8", $"ticket:{t}@"), HasCredentials: true),
            new("RTSP (VLC, lowest latency)", "Watch", $"rtsp://ticket:{t}@{h}:{Srv(s).RtspPort}/{s.Name}", HasCredentials: true),
            new("SRT", "Watch", $"srt://{h}:{Srv(s).SrtPort}?streamid=read:{s.Name}:ticket:{token}", HasCredentials: true),
        ];
    }

    private static string Enc(string v) => Uri.EscapeDataString(v);
}
