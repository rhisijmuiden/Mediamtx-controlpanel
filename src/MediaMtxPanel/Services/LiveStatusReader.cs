using MediaMtxPanel.Data;

namespace MediaMtxPanel.Services;

/// <summary>Live path state of several servers, read in parallel.</summary>
public class LiveSnapshot
{
    public Dictionary<int, Dictionary<string, MtxPath>> Paths { get; } = [];
    public Dictionary<int, MtxInfo> Info { get; } = [];
    public Dictionary<int, string> Errors { get; } = [];

    public MtxPath? Find(StreamDefinition s) =>
        Paths.TryGetValue(s.ServerId, out var paths) && paths.TryGetValue(s.Name, out var p) ? p : null;
}

public class LiveStatusReader(MediaMtxClientFactory clients, MediaMtxStatus status)
{
    public async Task<LiveSnapshot> ReadAsync(IEnumerable<MediaServer> servers, bool includeInfo, CancellationToken ct)
    {
        var snapshot = new LiveSnapshot();
        var results = await Task.WhenAll(servers.Where(s => s.Enabled).DistinctBy(s => s.Id).Select(async server =>
        {
            var mtx = clients.Create(server);
            try
            {
                var info = includeInfo ? await mtx.GetInfoAsync(ct) : null;
                var paths = await mtx.ListPathsAsync(ct);
                status.Report(server.Id, true, info?.Version, null);
                return (server.Id, info, paths: paths.ToDictionary(p => p.Name), error: (string?)null);
            }
            catch (Exception ex) when (ex is HttpRequestException or MediaMtxException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                status.Report(server.Id, false, null, ex.Message);
                return (server.Id, info: (MtxInfo?)null, paths: new Dictionary<string, MtxPath>(), error: ex.Message);
            }
        }));

        foreach (var r in results)
        {
            snapshot.Paths[r.Id] = r.paths;
            if (r.info is not null) snapshot.Info[r.Id] = r.info;
            if (r.error is not null) snapshot.Errors[r.Id] = r.error;
        }
        return snapshot;
    }
}
