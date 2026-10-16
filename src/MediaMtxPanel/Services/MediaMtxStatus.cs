using System.Collections.Concurrent;

namespace MediaMtxPanel.Services;

public record ServerState(bool Connected, string? Version, string? Error, DateTime LastCheck);

/// <summary>Last known connection state per MediaMTX server, shown in the UI.</summary>
public class MediaMtxStatus
{
    private readonly ConcurrentDictionary<int, ServerState> _states = new();

    public ServerState? Get(int serverId) => _states.GetValueOrDefault(serverId);

    public void Report(int serverId, bool connected, string? version, string? error)
    {
        var previous = Get(serverId);
        _states[serverId] = new ServerState(connected, version ?? previous?.Version, error, DateTime.UtcNow);
    }

    public void Forget(int serverId) => _states.TryRemove(serverId, out _);

    public IReadOnlyDictionary<int, ServerState> All => _states;
}
