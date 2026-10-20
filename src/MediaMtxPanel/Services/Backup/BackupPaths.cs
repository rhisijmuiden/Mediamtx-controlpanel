using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace MediaMtxPanel.Services.Backup;

/// <summary>Where the panel keeps the data a backup contains, and its own working files.</summary>
public sealed record BackupPaths(string DataDir, string DatabasePath, string SettingsPath, string? KeysPath)
{
    public const string DatabaseEntry = "panel.db";
    public const string SettingsEntry = "panel-settings.json";
    public const string KeysEntry = "keys/";

    public string WorkDir => Path.Combine(DataDir, "backup-work");
    public string StatePath => Path.Combine(DataDir, "backup-state.json");

    /// <summary>A verified restore waiting for the next start (see <see cref="PendingRestore"/>).</summary>
    public string PendingDir => Path.Combine(DataDir, "restore-pending");

    /// <summary>The data as it was just before the last restore, in case it has to be undone by hand.</summary>
    public string PreRestoreDir => Path.Combine(DataDir, "pre-restore");

    public static BackupPaths Resolve(IConfiguration config, string contentRoot)
    {
        var dataSource = new SqliteConnectionStringBuilder(config.GetConnectionString("DefaultConnection") ?? "").DataSource;
        var database = Path.GetFullPath(string.IsNullOrEmpty(dataSource) ? "panel.db" : dataSource, contentRoot);
        var keys = config["Panel:DataProtectionKeysPath"];
        // Without a configured folder ASP.NET keeps its keys in the home folder of the service user.
        var keysPath = !string.IsNullOrWhiteSpace(keys)
            ? Path.GetFullPath(keys, contentRoot)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspnet", "DataProtection-Keys");
        return new(Path.GetDirectoryName(database)!, database, PanelSettingsStore.ResolvePath(config, contentRoot), keysPath);
    }
}

/// <summary>Remembers the chain of the last full backup, so the next incremental backup knows what is already stored.</summary>
public sealed record BackupState
{
    /// <summary>Hash of the remote location and passphrase; a change starts a new chain with a full backup.</summary>
    public string? TargetSignature { get; init; }

    /// <summary>Archives of the current chain, full backup first.</summary>
    public List<string> Chain { get; init; } = [];

    /// <summary>Chunks stored in <see cref="Chain"/>.</summary>
    public HashSet<string> Chunks { get; init; } = [];

    public DateTimeOffset? LastRun { get; init; }
    public bool? LastSucceeded { get; init; }
    public string? LastError { get; init; }
    public string? LastArchive { get; init; }
    public long? LastSize { get; init; }
    public DateTimeOffset? LastSuccess { get; init; }

    /// <summary>Local date of the last scheduled run, so the schedule runs once a day.</summary>
    public DateOnly? LastScheduledDate { get; init; }

    public DateTimeOffset? LastRestore { get; init; }
    public string? LastRestoredArchive { get; init; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static BackupState Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<BackupState>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// A restore is downloaded and verified while the panel runs, then put in place at the next start, before
/// the database or the settings are opened. The data it replaces is kept in <see cref="BackupPaths.PreRestoreDir"/>.
/// </summary>
public static class PendingRestore
{
    public const string MarkerFile = "restore.json";

    /// <summary>Settings that belong to this installation rather than to the data, kept as they are.</summary>
    private static readonly string[] KeptSettings = ["Backup", "WebPort"];

    public sealed record Marker(string Archive, DateTimeOffset Created, string AppVersion);

    public static bool Apply(BackupPaths paths, Action<string> log)
    {
        var markerPath = Path.Combine(paths.PendingDir, MarkerFile);
        if (!File.Exists(markerPath))
        {
            // An interrupted download never got its marker; it is not a restore.
            if (Directory.Exists(paths.PendingDir))
                Directory.Delete(paths.PendingDir, recursive: true);
            return false;
        }
        var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath))!;
        log($"Restoring backup {marker.Archive}…");

        // Keep the current data next to it.
        if (Directory.Exists(paths.PreRestoreDir))
            Directory.Delete(paths.PreRestoreDir, recursive: true);
        Directory.CreateDirectory(paths.PreRestoreDir);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var file = paths.DatabasePath + suffix;
            if (File.Exists(file))
                File.Move(file, Path.Combine(paths.PreRestoreDir, Path.GetFileName(file)));
        }
        if (File.Exists(paths.SettingsPath))
            File.Copy(paths.SettingsPath, Path.Combine(paths.PreRestoreDir, Path.GetFileName(paths.SettingsPath)));

        File.Move(Path.Combine(paths.PendingDir, BackupPaths.DatabaseEntry), paths.DatabasePath);

        var restoredSettings = Path.Combine(paths.PendingDir, BackupPaths.SettingsEntry);
        if (File.Exists(restoredSettings))
        {
            var current = File.Exists(paths.SettingsPath) ? File.ReadAllText(paths.SettingsPath) : null;
            string merged;
            try
            {
                merged = MergeSettings(File.ReadAllText(restoredSettings), current);
            }
            catch (JsonException)
            {
                merged = File.ReadAllText(restoredSettings);
            }
            File.WriteAllText(paths.SettingsPath, merged);
        }

        // Keys are added, never removed: the current ones still decrypt the current backup login.
        var restoredKeys = Path.Combine(paths.PendingDir, "keys");
        if (paths.KeysPath is not null && Directory.Exists(restoredKeys))
        {
            Directory.CreateDirectory(paths.KeysPath);
            foreach (var key in Directory.EnumerateFiles(restoredKeys))
            {
                var target = Path.Combine(paths.KeysPath, Path.GetFileName(key));
                if (!File.Exists(target))
                    File.Copy(key, target);
            }
        }

        Directory.Delete(paths.PendingDir, recursive: true);
        var state = BackupState.Load(paths.StatePath);
        state = state with { LastRestore = DateTimeOffset.UtcNow, LastRestoredArchive = marker.Archive };
        state.Save(paths.StatePath);
        log($"Restored backup {marker.Archive} (made {marker.Created:u} by version {marker.AppVersion}). The previous data is in {paths.PreRestoreDir}.");
        return true;
    }

    internal static string MergeSettings(string restored, string? current)
    {
        var restoredNode = JsonNode.Parse(restored)?.AsObject() ?? new JsonObject();
        var currentNode = current is null ? null : JsonNode.Parse(current)?.AsObject();
        foreach (var name in KeptSettings)
        {
            restoredNode.Remove(name);
            if (currentNode?[name] is { } value)
                restoredNode[name] = value.DeepClone();
        }
        return restoredNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
