namespace MediaMtxPanel.Data;

/// <summary>
/// One measurement of a stream, written by <see cref="Services.StreamStatsCollector"/> every
/// collection interval while the stream is live. Bytes are the traffic since the previous sample.
/// </summary>
public class StreamStatSample
{
    public long Id { get; set; }

    public int StreamId { get; set; }
    public StreamDefinition? Stream { get; set; }

    /// <summary>Unix time in seconds; all streams sampled in one round share the same value.</summary>
    public long Timestamp { get; set; }

    /// <summary>Length of the period this sample covers.</summary>
    public int Seconds { get; set; }

    /// <summary>Viewers connected at the moment of the sample.</summary>
    public int Viewers { get; set; }

    /// <summary>Viewers on the restream platforms (YouTube, Twitch, Facebook) at the moment of the sample, where known.</summary>
    public int PlatformViewers { get; set; }

    /// <summary>Bytes received from the source during the period.</summary>
    public long BytesIn { get; set; }

    /// <summary>Bytes sent to viewers during the period.</summary>
    public long BytesOut { get; set; }
}
