using MediaMtxPanel.Data;

namespace MediaMtxPanel.Services;

public record StreamEndpoint(string Protocol, string Purpose, string Url, string? Note = null, bool HasCredentials = false);

public record ObsSettings(string Server, string StreamKey);

/// <summary>Options for wwwroot/js/player.js.</summary>
public record PlayerOptions(string Mode, string ReaderJsUrl, string WhepUrl, string HlsUrl, string User, string Pass);

/// <summary>Builds the public connection URLs for a stream, using the settings of the server it runs on.</summary>
public class StreamEndpointBuilder
{
    private static MediaServer Srv(StreamDefinition s) =>
        s.Server ?? throw new InvalidOperationException("Load the stream together with its server.");

    private static string Http(StreamDefinition s) => Srv(s).HttpsUrls ? "https" : "http";

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
            new("WebRTC (WHIP)", "Publish", $"{Http(s)}://{h}:{Srv(s).WebRtcPort}/{s.Name}/whip",
                "Use the publish username and password as HTTP Basic authentication (or as bearer token user:pass)."),
            new("WebRTC (browser)", "Publish", $"{Http(s)}://{h}:{Srv(s).WebRtcPort}/{s.Name}/publish",
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
            new("WebRTC (WHEP)", "Watch", $"{Http(s)}://{h}:{Srv(s).WebRtcPort}/{s.Name}/whep",
                s.PublicRead ? null : "Use the viewer credentials as HTTP Basic authentication."),
            new("WebRTC (browser)", "Watch", $"{Http(s)}://{h}:{Srv(s).WebRtcPort}/{s.Name}", httpNote),
            new("HLS", "Watch", $"{Http(s)}://{h}:{Srv(s).HlsPort}/{s.Name}/index.m3u8", httpNote),
            new("HLS (browser)", "Watch", $"{Http(s)}://{h}:{Srv(s).HlsPort}/{s.Name}", httpNote),
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
        var webrtc = $"{Http(s)}://{Srv(s).PublicHost}:{Srv(s).WebRtcPort}/{s.Name}";
        return new PlayerOptions(mode, $"{webrtc}/reader.js", $"{webrtc}/whep",
            $"{Http(s)}://{Srv(s).PublicHost}:{Srv(s).HlsPort}/{s.Name}/index.m3u8", user, pass);
    }

    /// <summary>URLs a paying viewer can open in VLC or another player, with the ticket as credentials.</summary>
    public IReadOnlyList<StreamEndpoint> Ticket(StreamDefinition s, string token)
    {
        var h = Srv(s).PublicHost;
        var t = Enc(token);
        return
        [
            new("HLS (VLC, smart TV, mobile)", "Watch", $"{Http(s)}://ticket:{t}@{h}:{Srv(s).HlsPort}/{s.Name}/index.m3u8", HasCredentials: true),
            new("RTSP (VLC, lowest latency)", "Watch", $"rtsp://ticket:{t}@{h}:{Srv(s).RtspPort}/{s.Name}", HasCredentials: true),
            new("SRT", "Watch", $"srt://{h}:{Srv(s).SrtPort}?streamid=read:{s.Name}:ticket:{token}", HasCredentials: true),
        ];
    }

    private static string Enc(string v) => Uri.EscapeDataString(v);
}
