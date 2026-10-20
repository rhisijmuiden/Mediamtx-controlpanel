namespace MediaMtxPanel.Data;

/// <summary>
/// Viewers of one stream (or of all streams together) on one day, for the viewer report. Built from the
/// statistics samples by <see cref="Services.ViewerReportService"/> and kept after the samples are deleted,
/// so the report goes back further than the statistics. Not linked to the stream: the history of a
/// deleted stream stays.
/// </summary>
public class ViewerDailyStat
{
    /// <summary><see cref="StreamId"/> of the row with the totals of all streams.</summary>
    public const int AllStreams = 0;

    public long Id { get; set; }

    /// <summary>Day in the time zone of the panel's server.</summary>
    public DateOnly Day { get; set; }

    public int StreamId { get; set; }

    /// <summary>Name of the stream on that day, for when the stream has been deleted since.</summary>
    public string StreamName { get; set; } = "";

    /// <summary>Most viewers at one moment.</summary>
    public int PeakViewers { get; set; }

    /// <summary>Different viewers (by IP address or ticket) that started watching.</summary>
    public int UniqueViewers { get; set; }

    /// <summary>Times someone started watching.</summary>
    public int Sessions { get; set; }

    /// <summary>Viewers × seconds: the total time watched.</summary>
    public long ViewerSeconds { get; set; }

    /// <summary>Seconds the stream was live.</summary>
    public long LiveSeconds { get; set; }

    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
}

/// <summary>
/// A viewer that started watching a stream on a day, written by the MediaMTX auth hook. The viewer is
/// only kept as a keyed hash of the IP address (and ticket), so the report can count unique viewers
/// without storing IP addresses.
/// </summary>
public class ViewerVisit
{
    public long Id { get; set; }
    public DateOnly Day { get; set; }
    public int StreamId { get; set; }
    public string ViewerKey { get; set; } = "";
}
