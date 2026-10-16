using MediaMtxPanel.Data;

namespace MediaMtxPanel.Services;

public record StreamEndpoint(string Protocol, string Purpose, string Url, string? Note = null, bool HasCredentials = false);

public record ObsSettings(string Server, string StreamKey);

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
            new("RTSP", "Publiceren", $"rtsp://{u}:{p}@{h}:{Srv(s).RtspPort}/{s.Name}", HasCredentials: true),
            new("RTMP", "Publiceren", $"rtmp://{h}:{Srv(s).RtmpPort}/{s.Name}?user={u}&pass={p}", HasCredentials: true),
            new("SRT", "Publiceren", $"srt://{h}:{Srv(s).SrtPort}?streamid=publish:{s.Name}:{s.PublishUser}:{s.PublishPassword}&pkt_size=1316", HasCredentials: true),
            new("WebRTC (WHIP)", "Publiceren", $"{Http(s)}://{h}:{Srv(s).WebRtcPort}/{s.Name}/whip",
                "Gebruik de publicatie-gebruikersnaam en het wachtwoord als HTTP Basic-authenticatie (of als bearer token user:pass)."),
            new("WebRTC (browser)", "Publiceren", $"{Http(s)}://{h}:{Srv(s).WebRtcPort}/{s.Name}/publish",
                "Open in de browser om vanaf webcam of scherm te publiceren."),
        ];
    }

    public IReadOnlyList<StreamEndpoint> Read(StreamDefinition s)
    {
        var h = Srv(s).PublicHost;
        var creds = s.PublicRead ? "" : $"{Enc(s.ReadUser)}:{Enc(s.ReadPassword)}@";
        var rtmpQuery = s.PublicRead ? "" : $"?user={Enc(s.ReadUser)}&pass={Enc(s.ReadPassword)}";
        var srtCreds = s.PublicRead ? "" : $":{s.ReadUser}:{s.ReadPassword}";
        var httpNote = s.PublicRead ? null : "De browser vraagt om de kijk-gebruikersnaam en het wachtwoord.";
        return
        [
            new("RTSP", "Bekijken", $"rtsp://{creds}{h}:{Srv(s).RtspPort}/{s.Name}", HasCredentials: !s.PublicRead),
            new("RTMP", "Bekijken", $"rtmp://{h}:{Srv(s).RtmpPort}/{s.Name}{rtmpQuery}", HasCredentials: !s.PublicRead),
            new("SRT", "Bekijken", $"srt://{h}:{Srv(s).SrtPort}?streamid=read:{s.Name}{srtCreds}", HasCredentials: !s.PublicRead),
            new("WebRTC (WHEP)", "Bekijken", $"{Http(s)}://{h}:{Srv(s).WebRtcPort}/{s.Name}/whep",
                s.PublicRead ? null : "Gebruik de kijk-gegevens als HTTP Basic-authenticatie."),
            new("WebRTC (browser)", "Bekijken", $"{Http(s)}://{h}:{Srv(s).WebRtcPort}/{s.Name}", httpNote),
            new("HLS", "Bekijken", $"{Http(s)}://{h}:{Srv(s).HlsPort}/{s.Name}/index.m3u8", httpNote),
            new("HLS (browser)", "Bekijken", $"{Http(s)}://{h}:{Srv(s).HlsPort}/{s.Name}", httpNote),
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

    private static string Enc(string v) => Uri.EscapeDataString(v);
}
