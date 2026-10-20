using System.Text.Json;
using System.Text.RegularExpressions;
using MediaMtxPanel.Options;
using MediaMtxPanel.Services.Backup;
using MediaMtxPanel.Services.Localization;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Services;

public enum UpdaterState { Idle, BackingUp, Starting, Running, Succeeded, Failed }

/// <summary>What the updater (deploy/mediamtx-panel-update.sh) writes to status.json.</summary>
public sealed record UpdaterStatusFile
{
    /// <summary>running, succeeded or failed.</summary>
    public string State { get; init; } = "";

    /// <summary>prepare, snapshot, install, done or rollback.</summary>
    public string Step { get; init; } = "";

    public string Message { get; init; } = "";
    public DateTimeOffset? Started { get; init; }
    public DateTimeOffset? Finished { get; init; }

    /// <summary>Local copy of the installation made before the update.</summary>
    public string Snapshot { get; init; } = "";
}

/// <summary>
/// "Update now" under Info. The panel runs without root rights, so it only asks for an update: it creates
/// a request file that a systemd path unit watches, and the updater installs the newest release of the
/// release channel as root, with a local copy and a roll-back, then restarts the panel. Only on Debian/LXC
/// installations made with the installers (which set the updater up); never in Docker.
/// </summary>
public sealed partial class PanelUpdater(
    IOptions<PanelOptions> options,
    BackupPaths paths,
    BackupService backups,
    WebPortInfo webPort,
    TextLocalizer L,
    ILogger<PanelUpdater> logger) : BackgroundService
{
    private const string FromVersionFile = "update-from-version";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset? requestedAt;

    public UpdaterState State { get; private set; } = UpdaterState.Idle;

    /// <summary>Why the last request could not be made (backup failed, no updater…), or null.</summary>
    public string? Error { get; private set; }

    public UpdaterStatusFile? LastRun { get; private set; }

    /// <summary>Version that was running when the last update was requested.</summary>
    public string? FromVersion { get; private set; }

    public IReadOnlyList<string> Log { get; private set; } = [];

    public event Action? Changed;

    private string StateDirectory => options.Value.UpdaterDirectory;
    private string RequestPath => Path.Combine(paths.DataDir, "update-request");

    /// <summary>The installers set the updater up; Docker and development runs have none.</summary>
    public bool Available =>
        OperatingSystem.IsLinux() && !webPort.InContainer && !string.IsNullOrWhiteSpace(StateDirectory)
        && Directory.Exists(StateDirectory) && File.Exists(options.Value.UpdaterCommand);

    public bool Busy => State is UpdaterState.BackingUp or UpdaterState.Starting or UpdaterState.Running;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Available)
            return;
        Refresh();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            if (State is UpdaterState.Starting or UpdaterState.Running)
                Refresh();
    }

    /// <summary>
    /// Makes a backup to the backup location first when asked (an incremental one, or full when there is no chain),
    /// then asks for the update. The panel restarts when the new version is installed.
    /// </summary>
    public async Task UpdateNowAsync(bool backupFirst, CancellationToken ct = default)
    {
        if (!Available || !await gate.WaitAsync(0, ct))
            return;
        try
        {
            if (Busy)
                return;
            Error = null;
            if (backupFirst && backups.Settings.IsConfigured)
            {
                Set(UpdaterState.BackingUp);
                var backup = await backups.BackupAsync(BackupKind.Incremental, ct);
                if (!backup.Success)
                {
                    Error = L["The backup failed, so nothing was updated: {0}", backup.Message];
                    Set(UpdaterState.Idle);
                    return;
                }
            }
            await File.WriteAllTextAsync(Path.Combine(paths.DataDir, FromVersionFile), BuildInfo.Version, ct);
            await File.WriteAllTextAsync(RequestPath, "update\n", ct);
            requestedAt = DateTimeOffset.UtcNow;
            logger.LogInformation("Update of the panel requested (version {Version})", BuildInfo.Version);
            Log = [];
            Set(UpdaterState.Starting);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not request the update");
            Error = L["Could not start the update: {0}", ex.Message];
            Set(UpdaterState.Idle);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reads the updater's status and log again.</summary>
    public void Refresh()
    {
        var status = ReadStatus();
        var fresh = status is not null && (requestedAt is null || status.Started >= requestedAt.Value.AddSeconds(-5));
        LastRun = status;
        FromVersion = ReadText(Path.Combine(paths.DataDir, FromVersionFile))?.Trim();
        if (State == UpdaterState.Starting && !fresh)
        {
            // The path unit picks the request up within a second; when it does not, the updater is not set up.
            if (DateTimeOffset.UtcNow - requestedAt > StartTimeout)
            {
                TryDelete(RequestPath);
                Error = L["The updater did not start. Update once from the command line to set it up."];
                Set(UpdaterState.Idle);
            }
            return;
        }
        Log = ReadLog();
        var state = status?.State switch
        {
            "running" => UpdaterState.Running,
            "succeeded" => UpdaterState.Succeeded,
            "failed" => UpdaterState.Failed,
            _ => UpdaterState.Idle,
        };
        if (State != UpdaterState.BackingUp)
            Set(state);
        else
            Changed?.Invoke();
    }

    private void Set(UpdaterState state)
    {
        State = state;
        Changed?.Invoke();
    }

    private UpdaterStatusFile? ReadStatus()
    {
        var text = ReadText(Path.Combine(StateDirectory, "status.json"));
        if (text is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<UpdaterStatusFile>(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private List<string> ReadLog()
    {
        var text = ReadText(Path.Combine(StateDirectory, "update.log"));
        if (string.IsNullOrEmpty(text))
            return [];
        // The installers colour their output for a terminal.
        return Ansi().Replace(text, "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(300).ToList();
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();
}
