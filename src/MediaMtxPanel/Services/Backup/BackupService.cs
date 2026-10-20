using System.Security.Cryptography;
using System.Text;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services.Localization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace MediaMtxPanel.Services.Backup;

public sealed record BackupResult(bool Success, string Message, string? Archive = null);

/// <summary>An archive at the remote location; <see cref="Needs"/> are the archives a restore of it downloads.</summary>
public sealed record BackupEntry(RemoteBackupFile File, BackupName Name, IReadOnlyList<RemoteBackupFile> Needs)
{
    /// <summary>False when an archive of its chain is missing at the remote location.</summary>
    public bool Complete { get; init; } = true;
    public long RestoreSize => Needs.Sum(n => n.Size);
}

/// <summary>
/// Full and incremental backups of the panel (database, settings and encryption keys) to FTP, FTPS, SCP, SFTP
/// or a local folder, the daily schedule, and restores from the remote location.
/// </summary>
public sealed class BackupService(
    PanelSettingsStore store,
    BackupPaths paths,
    IConfiguration config,
    IDataProtectionProvider dataProtection,
    IServiceScopeFactory scopes,
    MailService mailer,
    TextLocalizer L,
    ILogger<BackupService> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lock logGate = new();
    private readonly List<string> log = [];

    private IDataProtector Protector => dataProtection.CreateProtector("MediaMtxPanel.Backup.v1");

    public BackupSettings Settings => store.Current.Backup ?? new();
    public BackupState State => BackupState.Load(paths.StatePath);
    public BackupPaths Paths => paths;

    /// <summary>What is running right now ("Full backup", "Restore"…), or null.</summary>
    public string? Activity { get; private set; }

    /// <summary>Log of the running or last operation.</summary>
    public IReadOnlyList<string> Log
    {
        get { lock (logGate) return log.ToList(); }
    }

    /// <summary>Raised when <see cref="Activity"/> or <see cref="Log"/> change; may run on any thread.</summary>
    public event Action? Changed;

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : Protector.Protect(secret);

    public string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
            return null;
        try
        {
            return Protector.Unprotect(protectedSecret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public BackupLogin LoginFor(BackupTarget target) =>
        new(target, Unprotect(target.PasswordProtected), Unprotect(target.PrivateKeyProtected), Unprotect(target.KeyPassphraseProtected));

    /// <summary>True when the encryption keys are kept in a folder that a restore can put back.</summary>
    public bool KeysIncluded => paths.KeysPath is not null && Directory.Exists(paths.KeysPath);

    // ---------------------------------------------------------------- connection test and listing

    /// <summary>Connects, creates the folder and lists it. <paramref name="hostKey"/> gets the SSH fingerprint when one was seen.</summary>
    public async Task<(BackupResult Result, string? HostKey)> TestAsync(BackupLogin login, CancellationToken ct = default)
    {
        string? seen = null;
        try
        {
            await using var storage = BackupStorage.Create(login, k => seen = k);
            await storage.ConnectAsync(ct);
            var files = await storage.ListAsync(ct);
            await ProbeWriteAsync(storage, ct);
            return (new(true, L["Connected. The folder is writable and holds {0} backup(s).", files.Count]), seen);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (new(false, Describe(ex, login.Target)), seen);
        }
    }

    /// <summary>All backups at the configured location, newest first.</summary>
    public async Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default)
    {
        var settings = Settings;
        await using var storage = await ConnectAsync(settings, ct);
        return Group(await storage.ListAsync(ct));
    }

    internal static IReadOnlyList<BackupEntry> Group(IReadOnlyList<RemoteBackupFile> files)
    {
        var parsed = files
            .Select(f => (File: f, Ok: BackupName.TryParse(f.Name, out var n), Name: n))
            .Where(x => x.Ok)
            .ToList();
        var entries = new List<BackupEntry>();
        foreach (var (file, _, name) in parsed)
        {
            var chain = parsed
                .Where(p => p.Name.FullCreated == name.FullCreated && p.Name.Created <= name.Created)
                .OrderBy(p => p.Name.Created)
                .Select(p => p.File)
                .ToList();
            var complete = chain.Count > 0 && chain[0].Name == name.FullName;
            entries.Add(new(file, name, chain) { Complete = complete });
        }
        return entries.OrderByDescending(e => e.Name.Created).ToList();
    }

    // ---------------------------------------------------------------- backup

    /// <summary>Makes a backup. An incremental backup becomes a full one when there is no usable chain.</summary>
    public async Task<BackupResult> BackupAsync(BackupKind requested, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct))
            return new(false, L["Another backup or restore is running."]);
        var started = DateTimeOffset.UtcNow;
        try
        {
            Begin(requested == BackupKind.Full ? "Full backup" : "Incremental backup");
            var settings = Settings;
            if (!settings.IsConfigured)
                return Finish(new(false, L["Set up a backup location first."]), started);

            var result = await Task.Run(() => BackupCoreAsync(settings, requested, ct), ct);
            return Finish(result, started);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Backup failed");
            Write($"Failed: {ex.Message}");
            return Finish(new(false, Describe(ex, Settings.Target)), started);
        }
        finally
        {
            Activity = null;
            Changed?.Invoke();
            gate.Release();
        }
    }

    private async Task<BackupResult> BackupCoreAsync(BackupSettings settings, BackupKind requested, CancellationToken ct)
    {
        var passphrase = Unprotect(settings.PassphraseProtected);
        if (settings.PassphraseProtected is not null && passphrase is null)
            return new(false, L["The backup passphrase cannot be decrypted. Enter it again under Backup."]);

        var work = ResetFolder(paths.WorkDir);
        try
        {
            Write("Taking a snapshot of the database…");
            var snapshot = Path.Combine(work, BackupPaths.DatabaseEntry);
            SnapshotDatabase(snapshot);
            var sources = new List<(string Entry, string Path)> { (BackupPaths.DatabaseEntry, snapshot) };
            if (File.Exists(paths.SettingsPath))
                sources.Add((BackupPaths.SettingsEntry, paths.SettingsPath));
            if (KeysIncluded)
                foreach (var key in Directory.EnumerateFiles(paths.KeysPath!, "*.xml").Order())
                    sources.Add((BackupPaths.KeysEntry + Path.GetFileName(key), key));

            await using var storage = await ConnectAsync(settings, ct);
            var remote = await storage.ListAsync(ct);
            var state = State;
            var signature = Signature(settings.Target, passphrase);
            var kind = requested;
            if (kind == BackupKind.Incremental)
            {
                var names = remote.Select(r => r.Name).ToHashSet();
                if (state.TargetSignature != signature || state.Chain.Count == 0 || !state.Chain.All(names.Contains))
                {
                    Write("No complete full backup at this location with these settings: making a full backup instead.");
                    kind = BackupKind.Full;
                }
            }

            // Names have whole seconds; never reuse the time of an existing archive.
            var now = DateTime.UtcNow;
            var latest = remote.Select(r => BackupName.TryParse(r.Name, out var n) ? n.Created : DateTime.MinValue).DefaultIfEmpty(DateTime.MinValue).Max();
            if (now < latest.AddSeconds(1))
                now = latest.AddSeconds(1);
            var name = kind == BackupKind.Full
                ? BackupName.Full(now)
                : BackupName.Incremental(now, BackupName.TryParse(state.Chain[0], out var full) ? full.Created : now);
            var known = kind == BackupKind.Full ? new HashSet<string>() : new HashSet<string>(state.Chunks);

            Write("Reading the data…");
            var files = sources.Select(s => new BackupFileEntry(s.Entry, new FileInfo(s.Path).Length,
                BackupArchive.HashFile(s.Path), BackupArchive.ReadChunks(s.Path).Select(c => c.Hash).ToList())).ToList();
            var all = files.SelectMany(f => f.Chunks).ToHashSet();
            var newChunks = all.Count(h => !known.Contains(h));
            Write(kind == BackupKind.Full
                ? $"{files.Count} files, {all.Count} chunks."
                : $"{files.Count} files, {newChunks} of {all.Count} chunks changed since the previous backup.");

            var manifest = new BackupManifest
            {
                Name = name.ToString(),
                Kind = kind,
                Created = now,
                AppVersion = BuildInfo.Version,
                Base = kind == BackupKind.Full ? [] : [.. state.Chain],
                Files = files,
            };
            var archive = Path.Combine(work, name.ToString());
            BackupArchive.Write(archive, manifest, NewChunks(sources, known), passphrase);
            var size = new FileInfo(archive).Length;
            Write($"Uploading {name} ({Format.Bytes((ulong)size)})…");
            await storage.UploadAsync(archive, name.ToString(), ct);

            var chain = kind == BackupKind.Full ? [name.ToString()] : state.Chain.Append(name.ToString()).ToList();
            known.UnionWith(all);
            Update(s => s with { TargetSignature = signature, Chain = chain, Chunks = known, LastArchive = name.ToString(), LastSize = size });
            Write("Uploaded.");

            await ApplyRetentionAsync(storage, settings.KeepFullBackups, ct);
            return new(true, kind == requested
                ? L["Backup {0} uploaded ({1}).", name.ToString(), Format.Bytes((ulong)size)]
                : L["There was no full backup to build on, so a full backup was made: {0} ({1}).", name.ToString(), Format.Bytes((ulong)size)], name.ToString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(work);
        }
    }

    /// <summary>Chunks not stored yet, read again from the files so a large database never sits in memory.</summary>
    private static IEnumerable<KeyValuePair<string, byte[]>> NewChunks(List<(string Entry, string Path)> sources, HashSet<string> known)
    {
        var written = new HashSet<string>(known);
        foreach (var (_, path) in sources)
            foreach (var (hash, data) in BackupArchive.ReadChunks(path))
                if (written.Add(hash))
                    yield return new(hash, data);
    }

    private void SnapshotDatabase(string target)
    {
        // SQLite's online backup copies a consistent state while the panel keeps running.
        using var source = new SqliteConnection(config.GetConnectionString("DefaultConnection"));
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private async Task ApplyRetentionAsync(IBackupStorage storage, int keep, CancellationToken ct)
    {
        var files = await storage.ListAsync(ct);
        var parsed = files.Select(f => (f, ok: BackupName.TryParse(f.Name, out var n), n)).Where(x => x.ok).ToList();
        var kept = parsed.Where(p => p.n.Kind == BackupKind.Full)
            .OrderByDescending(p => p.n.Created)
            .Take(Math.Max(1, keep))
            .Select(p => p.n.FullCreated)
            .ToHashSet();
        if (kept.Count == 0)
            return;
        var oldestKept = kept.Min();
        // Older chains go, and so do older incremental backups whose full backup is gone (they cannot be restored).
        foreach (var (file, _, _) in parsed.Where(p => !kept.Contains(p.n.FullCreated) && p.n.Created < oldestKept))
        {
            Write($"Removing old backup {file.Name}…");
            await storage.DeleteAsync(file.Name, ct);
        }
    }

    // ---------------------------------------------------------------- restore

    /// <summary>
    /// Downloads a backup with every archive it builds on, checks it and stages it in
    /// <see cref="BackupPaths.PendingDir"/>. It replaces the data when the panel starts again.
    /// </summary>
    public async Task<BackupResult> PrepareRestoreAsync(string archiveName, string? passphrase, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct))
            return new(false, L["Another backup or restore is running."]);
        try
        {
            Begin("Restore");
            passphrase = string.IsNullOrEmpty(passphrase) ? Unprotect(Settings.PassphraseProtected) : passphrase;
            return await Task.Run(() => RestoreCoreAsync(archiveName, passphrase, ct), ct);
        }
        catch (BackupPassphraseException ex)
        {
            Write($"Failed: {ex.Message}");
            return new(false, L[ex.Message]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Restore of {Archive} failed", archiveName);
            Write($"Failed: {ex.Message}");
            TryDelete(paths.PendingDir);
            return new(false, Describe(ex, Settings.Target));
        }
        finally
        {
            Activity = null;
            Changed?.Invoke();
            gate.Release();
        }
    }

    private async Task<BackupResult> RestoreCoreAsync(string archiveName, string? passphrase, CancellationToken ct)
    {
        if (!BackupName.TryParse(archiveName, out _))
            return new(false, L["'{0}' is not a panel backup.", archiveName]);

        var work = ResetFolder(paths.WorkDir);
        try
        {
            await using var storage = await ConnectAsync(Settings, ct);
            var remote = (await storage.ListAsync(ct)).Select(f => f.Name).ToHashSet();
            if (!remote.Contains(archiveName))
                return new(false, L["Backup {0} is no longer at the remote location.", archiveName]);

            Write($"Downloading {archiveName}…");
            var targetPath = Path.Combine(work, archiveName);
            await storage.DownloadAsync(archiveName, targetPath, ct);
            var manifest = BackupArchive.ReadManifest(targetPath, passphrase);
            if (Version.TryParse(manifest.AppVersion, out var made) && Version.TryParse(BuildInfo.Version, out var running) && made > running)
                return new(false, L["This backup was made by version {0}. Update the panel to that version or newer before restoring it.", manifest.AppVersion]);
            var missing = manifest.Base.Where(b => !remote.Contains(b)).ToList();
            if (missing.Count > 0)
                return new(false, L["This backup builds on {0}, which is missing at the remote location.", string.Join(", ", missing)]);

            var chunks = Directory.CreateDirectory(Path.Combine(work, "chunks")).FullName;
            foreach (var b in manifest.Base)
            {
                Write($"Downloading {b}…");
                var local = Path.Combine(work, b);
                await storage.DownloadAsync(b, local, ct);
                BackupArchive.Extract(local, passphrase, chunks);
                File.Delete(local);
            }
            Write("Checking the archives…");
            BackupArchive.Extract(targetPath, passphrase, chunks);

            var pending = ResetFolder(paths.PendingDir);
            foreach (var file in manifest.Files)
            {
                if (file.Path.Contains("..") || Path.IsPathRooted(file.Path))
                    throw new InvalidDataException($"Unexpected path {file.Path} in the backup.");
                var output = Path.Combine(pending, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using (var stream = File.Create(output))
                    foreach (var hash in file.Chunks)
                    {
                        var chunk = Path.Combine(chunks, hash);
                        if (!File.Exists(chunk))
                            throw new InvalidDataException($"Chunk {hash} of {file.Path} is missing from the backup.");
                        using var c = File.OpenRead(chunk);
                        c.CopyTo(stream);
                    }
                if (BackupArchive.HashFile(output) != file.Sha256)
                    throw new InvalidDataException($"{file.Path} does not match its checksum.");
            }

            var database = Path.Combine(pending, BackupPaths.DatabaseEntry);
            if (!File.Exists(database))
                throw new InvalidDataException("The backup contains no database.");
            Write("Checking the database…");
            CheckDatabase(database);

            File.WriteAllText(Path.Combine(pending, PendingRestore.MarkerFile), System.Text.Json.JsonSerializer.Serialize(
                new PendingRestore.Marker(archiveName, manifest.Created, manifest.AppVersion)));
            Write("The backup is ready. It replaces the current data when the panel restarts.");
            return new(true, L["Backup {0} is downloaded and checked. The panel restarts to finish the restore.", archiveName], archiveName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(work);
        }
    }

    private static void CheckDatabase(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        var result = command.ExecuteScalar() as string;
        if (result != "ok")
            throw new InvalidDataException($"The database in the backup is damaged: {result}");
    }

    /// <summary>True when a restore is staged and waits for a restart.</summary>
    public bool RestorePending => File.Exists(Path.Combine(paths.PendingDir, PendingRestore.MarkerFile));

    public void CancelPendingRestore() => TryDelete(paths.PendingDir);

    // ---------------------------------------------------------------- schedule

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                try
                {
                    await RunScheduledAsync(DateTime.Now, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Scheduled backup failed");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    internal async Task RunScheduledAsync(DateTime localNow, CancellationToken ct)
    {
        var settings = Settings;
        if (!settings.ScheduleEnabled || !settings.IsConfigured || RestorePending)
            return;
        var today = DateOnly.FromDateTime(localNow);
        if (TimeOnly.FromDateTime(localNow) < settings.TimeOfDay || State.LastScheduledDate == today)
            return;

        Update(s => s with { LastScheduledDate = today });
        var kind = settings.FullBackupDay is null || settings.FullBackupDay == localNow.DayOfWeek ? BackupKind.Full : BackupKind.Incremental;
        logger.LogInformation("Starting the scheduled {Kind} backup", kind);
        var result = await BackupAsync(kind, ct);
        if (result.Success)
            logger.LogInformation("Scheduled backup done: {Message}", result.Message);
        else
        {
            logger.LogWarning("Scheduled backup failed: {Message}", result.Message);
            if (settings.MailOnFailure)
                await MailFailureAsync(result.Message);
        }
    }

    private async Task MailFailureAsync(string message)
    {
        if (!mailer.IsConfigured)
            return;
        try
        {
            using var scope = scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var language = store.Current.DefaultLanguage;
            var app = store.Current.AppName;
            var subject = string.Format(L.Translate("Backup of {0} failed", language), app);
            var body = $"<p>{System.Net.WebUtility.HtmlEncode(string.Format(L.Translate("The scheduled backup of {0} failed:", language), app))}</p>" +
                       $"<p><b>{System.Net.WebUtility.HtmlEncode(message)}</b></p>";
            foreach (var admin in await users.GetUsersInRoleAsync(Roles.Admin))
                if (!string.IsNullOrEmpty(admin.Email))
                    await mailer.SendAsync(admin.Email, subject, body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not mail the administrators about the failed backup");
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task<IBackupStorage> ConnectAsync(BackupSettings settings, CancellationToken ct)
    {
        if (!settings.IsConfigured)
            throw new InvalidOperationException(L["Set up a backup location first."]);
        var storage = BackupStorage.Create(LoginFor(settings.Target), TrustHostKey);
        try
        {
            if (Activity is not null)
                Write($"Connecting to {Where(settings.Target)}…");
            await storage.ConnectAsync(ct);
            return storage;
        }
        catch
        {
            await storage.DisposeAsync();
            throw;
        }
    }

    /// <summary>The first SSH connection of a saved location remembers the server's key.</summary>
    private void TrustHostKey(string fingerprint)
    {
        Write($"Trusting host key {fingerprint}.");
        var backup = Settings;
        store.Save(store.Current with { Backup = backup with { Target = backup.Target with { HostKeyFingerprint = fingerprint } } });
    }

    private static async Task ProbeWriteAsync(IBackupStorage storage, CancellationToken ct)
    {
        // Upload and delete a tiny file under a name that never looks like a backup.
        var local = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(local, "MediaMTX Panel write test", ct);
            const string probe = "mediamtx-panel_write-test.tmp";
            await storage.UploadAsync(local, probe, ct);
            await storage.DeleteAsync(probe, ct);
        }
        finally
        {
            File.Delete(local);
        }
    }

    internal static string Where(BackupTarget t) => t.Protocol == BackupProtocol.Local
        ? t.Folder
        : $"{t.Protocol.ToString().ToLowerInvariant()}://{(string.IsNullOrEmpty(t.UserName) ? "" : t.UserName + "@")}{t.Host}:{t.Port}/{t.Folder.TrimStart('/')}";

    private static string Signature(BackupTarget t, string? passphrase) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{t.Protocol}|{t.Host.Trim().ToLowerInvariant()}|{t.Port}|{t.UserName}|{t.Folder.Trim()}|{passphrase}")));

    private string Describe(Exception ex, BackupTarget target) => ex switch
    {
        BackupHostKeyException hk => L["The host key of {0} changed to {1}. If the server was reinstalled, forget the old key under Backup and connect again.", target.Host, hk.Message],
        Renci.SshNet.Common.SshAuthenticationException => L["The server refused the login for '{0}'. Check the user, password or key.", target.UserName ?? ""],
        FluentFTP.Exceptions.FtpAuthenticationException => L["The server refused the login for '{0}'. Check the user, password or key.", target.UserName ?? ""],
        System.Net.Sockets.SocketException or TimeoutException or Renci.SshNet.Common.SshOperationTimeoutException =>
            L["Cannot reach {0}: {1}", $"{target.Host}:{target.Port}", ex.Message],
        System.Security.Authentication.AuthenticationException =>
            L["The TLS certificate of {0} is not trusted: {1}", target.Host, ex.Message],
        BackupPassphraseException => L[ex.Message],
        _ => L["The backup location reported an error: {0}", ex.InnerException?.Message ?? ex.Message],
    };

    private BackupResult Finish(BackupResult result, DateTimeOffset started)
    {
        Update(s => s with
        {
            LastRun = started,
            LastSucceeded = result.Success,
            LastError = result.Success ? null : result.Message,
            LastSuccess = result.Success ? started : s.LastSuccess,
        });
        return result;
    }

    private readonly Lock stateGate = new();

    private void Update(Func<BackupState, BackupState> change)
    {
        lock (stateGate)
            change(BackupState.Load(paths.StatePath)).Save(paths.StatePath);
    }

    private void Begin(string activity)
    {
        lock (logGate) log.Clear();
        Activity = activity;
        Changed?.Invoke();
    }

    private void Write(string line)
    {
        lock (logGate) log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        Changed?.Invoke();
    }

    private static string ResetFolder(string path)
    {
        TryDelete(path);
        return Directory.CreateDirectory(path).FullName;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
    }
}
