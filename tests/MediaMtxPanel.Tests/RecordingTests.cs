using MediaMtxPanel.Data;
using MediaMtxPanel.Services;
using MediaMtxPanel.Services.Setup;

namespace MediaMtxPanel.Tests;

public class RecordingTests
{
    [Theory]
    [InlineData("7d", 7 * 24 * 3600)]
    [InlineData("1d2h0m0s", 26 * 3600)]
    [InlineData("24h0m0s", 24 * 3600)]
    [InlineData("1h30m", 5400)]
    [InlineData("0s", 0)]
    [InlineData("1.5s", 1.5)]
    public void Parses_MediaMTX_durations(string value, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), MtxDuration.Parse(value));

    [Fact]
    public void Retention_is_written_and_compared_as_a_duration()
    {
        var stream = new StreamDefinition { Record = true, RecordRetentionDays = 2 };
        var desired = StreamService.ToConfig(stream);
        Assert.Equal("2d", desired.RecordDeleteAfter);
        Assert.True(StreamService.Matches(new MtxPathConfig { Source = "publisher", Record = true, RecordDeleteAfter = "48h0m0s" }, desired));
        Assert.False(StreamService.Matches(new MtxPathConfig { Source = "publisher", Record = true, RecordDeleteAfter = "1d" }, desired));

        stream.RecordRetentionDays = 0;
        Assert.Equal("0s", StreamService.ToConfig(stream).RecordDeleteAfter);
    }

    [Theory]
    [InlineData("http://10.0.0.5:9997", null, "http://10.0.0.5:9996")]
    [InlineData("https://mtx.example.com/", null, "https://mtx.example.com:9996")]
    [InlineData("http://10.0.0.5:9997", "http://10.0.0.5:19996/", "http://10.0.0.5:19996")]
    public void Playback_url_defaults_to_the_api_host(string api, string? playback, string expected) =>
        Assert.Equal(expected, new MediaServer { ApiUrl = api, PlaybackUrl = playback }.PlaybackBaseUrl());

    [Fact]
    public void Ssh_setup_turns_on_playback()
    {
        var yaml = MediaMtxConfigEditor.ConfigureForPanel("playback: no\nplaybackAddress: :9996\n", "http://panel:8080/api/mediamtx/auth?secret=x", 9997, null);
        Assert.Equal("yes", MediaMtxConfigEditor.Get(yaml, "playback"));
        Assert.Equal(":9996", MediaMtxConfigEditor.Get(yaml, "playbackAddress"));
    }

    [Fact]
    public void Recording_query_keeps_sub_second_start()
    {
        var r = new Recording(DateTimeOffset.Parse("2026-10-19T23:55:12.663168Z"), TimeSpan.FromSeconds(11.2));
        Assert.Equal("start=2026-10-19T23%3A55%3A12.6631680Z&duration=11.2", r.Query);
    }
}
