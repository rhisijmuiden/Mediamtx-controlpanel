using MediaMtxPanel.Data;
using MediaMtxPanel.Services;
using MediaMtxPanel.Services.Backup;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MediaMtxPanel.Tests;

public class BackupNameTests
{
    [Fact]
    public void Names_round_trip()
    {
        var full = BackupName.Full(new DateTime(2026, 10, 19, 3, 0, 0, DateTimeKind.Utc));
        var incr = BackupName.Incremental(new DateTime(2026, 10, 20, 3, 0, 5, DateTimeKind.Utc), full.Created);

        Assert.Equal("mediamtx-panel_20261019-030000_full.tar", full.ToString());
        Assert.Equal("mediamtx-panel_20261020-030005_incr_20261019-030000.tar", incr.ToString());
        Assert.True(BackupName.TryParse(incr.ToString(), out var parsed));
        Assert.Equal(incr, parsed);
        Assert.Equal(full.ToString(), parsed.FullName);
        Assert.False(BackupName.TryParse("mediamtx-panel_20261019-030000_full.tar.part", out _));
        Assert.False(BackupName.TryParse("mediamtx-panel_20261019-030000_incr.tar", out _));
    }

    [Fact]
    public void Group_lists_the_chain_a_restore_needs()
    {
        RemoteBackupFile F(string name) => new(name, 10, null);
        var entries = BackupService.Group([
            F("mediamtx-panel_20261019-030000_full.tar"),
            F("mediamtx-panel_20261020-030000_incr_20261019-030000.tar"),
            F("mediamtx-panel_20261021-030000_incr_20261019-030000.tar"),
            F("mediamtx-panel_20261026-030000_full.tar"),
            F("mediamtx-panel_20261018-030000_incr_20261011-030000.tar"),
            F("unrelated.txt"),
        ]);

        Assert.Equal(5, entries.Count);
        Assert.Equal("mediamtx-panel_20261026-030000_full.tar", entries[0].File.Name);
        var second = entries.Single(e => e.File.Name.StartsWith("mediamtx-panel_20261021"));
        Assert.Equal(3, second.Needs.Count);
        Assert.True(second.Complete);
        Assert.False(entries.Single(e => e.File.Name.StartsWith("mediamtx-panel_20261018")).Complete);
    }
}

public class BackupArchiveTests
{
    [Fact]
    public void Encrypted_archive_needs_the_right_passphrase()
    {
        var dir = Directory.CreateTempSubdirectory("backup-archive-").FullName;
        try
        {
            var data = new byte[200_000];
            Random.Shared.NextBytes(data);
            var source = Path.Combine(dir, "data.bin");
            File.WriteAllBytes(source, data);
            var chunks = BackupArchive.ReadChunks(source).ToList();
            Assert.Equal(4, chunks.Count);

            var manifest = new BackupManifest
            {
                Name = "test", Kind = BackupKind.Full, Created = DateTimeOffset.UtcNow, AppVersion = "1.0.0",
                Files = [new("data.bin", data.Length, BackupArchive.HashFile(source), chunks.Select(c => c.Hash).ToList())],
            };
            var archive = Path.Combine(dir, "a.tar");
            BackupArchive.Write(archive, manifest, chunks.Select(c => new KeyValuePair<string, byte[]>(c.Hash, c.Data)), "correct horse");

            // Nothing readable without the passphrase.
            var raw = File.ReadAllBytes(archive);
            Assert.DoesNotContain("data.bin", System.Text.Encoding.UTF8.GetString(raw));
            Assert.Throws<BackupPassphraseException>(() => BackupArchive.ReadManifest(archive, null));
            Assert.Throws<BackupPassphraseException>(() => BackupArchive.ReadManifest(archive, "wrong"));

            var output = Directory.CreateDirectory(Path.Combine(dir, "chunks")).FullName;
            var read = BackupArchive.Extract(archive, "correct horse", output);
            Assert.Equal("data.bin", read.Files.Single().Path);
            var rebuilt = read.Files[0].Chunks.SelectMany(h => File.ReadAllBytes(Path.Combine(output, h))).ToArray();
            Assert.Equal(data, rebuilt);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Restore_keeps_the_backup_settings_of_this_installation()
    {
        var merged = PendingRestore.MergeSettings(
            """{"AppName":"Old","WebPort":9000,"Backup":{"Time":"01:00"}}""",
            """{"AppName":"New","WebPort":8080,"Backup":{"Time":"04:00"}}""");
        Assert.Contains("\"AppName\": \"Old\"", merged);
        Assert.Contains("8080", merged);
        Assert.Contains("04:00", merged);
        Assert.DoesNotContain("01:00", merged);
    }
}

public class BackupServiceTests
{
    [Fact]
    public async Task Full_and_incremental_backups_restore_to_the_state_they_were_made_in()
    {
        using var factory = new PanelFactory();
        _ = factory.Client();
        var services = factory.Services;
        var backups = services.GetRequiredService<BackupService>();
        var store = services.GetRequiredService<PanelSettingsStore>();
        var folder = Path.Combine(backups.Paths.DataDir, "remote");
        store.Save(store.Current with
        {
            AppName = "Before",
            Backup = new BackupSettings
            {
                Target = new BackupTarget { Protocol = BackupProtocol.Local, Folder = folder },
                PassphraseProtected = backups.Protect("secret passphrase"),
            },
        });

        var test = await backups.TestAsync(backups.LoginFor(backups.Settings.Target));
        Assert.True(test.Result.Success, test.Result.Message);

        // An incremental backup without a full one becomes a full backup.
        var first = await backups.BackupAsync(BackupKind.Incremental);
        Assert.True(first.Success, first.Message);
        Assert.EndsWith("_full.tar", first.Archive);

        await AddUserAsync(services, "second@example.com");
        var second = await backups.BackupAsync(BackupKind.Incremental);
        Assert.True(second.Success, second.Message);
        Assert.Contains("_incr_", second.Archive);
        // Only changed chunks: the incremental archive is smaller than the full one.
        Assert.True(new FileInfo(Path.Combine(folder, second.Archive!)).Length < new FileInfo(Path.Combine(folder, first.Archive!)).Length);

        await AddUserAsync(services, "third@example.com");
        var third = await backups.BackupAsync(BackupKind.Incremental);
        Assert.True(third.Success, third.Message);

        var list = await backups.ListAsync();
        Assert.Equal(3, list.Count);
        Assert.Equal(3, list[0].Needs.Count);

        store.Save(store.Current with { AppName = "After" });

        // Restoring the second backup needs the full one and the first incremental one.
        var restore = await backups.PrepareRestoreAsync(second.Archive!, null);
        Assert.True(restore.Success, restore.Message);
        Assert.True(backups.RestorePending);
        Assert.Equal(2, CountUsers(Path.Combine(backups.Paths.PendingDir, BackupPaths.DatabaseEntry)));

        // Apply it to a copy of the data folder, as the next start would.
        var copy = Directory.CreateTempSubdirectory("backup-apply-").FullName;
        try
        {
            var paths = new BackupPaths(copy, Path.Combine(copy, "panel.db"), Path.Combine(copy, "panel-settings.json"), Path.Combine(copy, "keys"));
            File.Copy(store.FilePath, paths.SettingsPath);
            CopyFolder(backups.Paths.PendingDir, paths.PendingDir);
            Assert.True(PendingRestore.Apply(paths, _ => { }));
            Assert.False(Directory.Exists(paths.PendingDir));
            Assert.Equal(2, CountUsers(paths.DatabasePath));
            var settings = PanelSettingsStore.Load(paths.SettingsPath);
            Assert.Equal("Before", settings.AppName);
            Assert.Equal(BackupProtocol.Local, settings.Backup?.Target.Protocol);
            Assert.NotEmpty(Directory.GetFiles(paths.KeysPath!, "*.xml"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(copy, recursive: true);
        }

        // A wrong passphrase is refused and nothing is staged.
        backups.CancelPendingRestore();
        var wrong = await backups.PrepareRestoreAsync(second.Archive!, "wrong");
        Assert.False(wrong.Success);
        Assert.False(backups.RestorePending);
    }

    [Fact]
    public async Task Old_full_backups_are_removed_with_their_incremental_backups()
    {
        using var factory = new PanelFactory();
        _ = factory.Client();
        var backups = factory.Services.GetRequiredService<BackupService>();
        var store = factory.Services.GetRequiredService<PanelSettingsStore>();
        var folder = Path.Combine(backups.Paths.DataDir, "remote");
        Directory.CreateDirectory(folder);
        foreach (var old in new[] { "mediamtx-panel_20200101-030000_full.tar", "mediamtx-panel_20200102-030000_incr_20200101-030000.tar",
                                    "mediamtx-panel_20200108-030000_full.tar" })
            File.WriteAllText(Path.Combine(folder, old), "old");
        store.Save(store.Current with
        {
            Backup = new BackupSettings { Target = new() { Protocol = BackupProtocol.Local, Folder = folder }, KeepFullBackups = 2 },
        });

        var result = await backups.BackupAsync(BackupKind.Full);
        Assert.True(result.Success, result.Message);

        var left = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["mediamtx-panel_20200108-030000_full.tar", result.Archive], left);
    }

    [Fact]
    public async Task The_schedule_runs_once_a_day_after_the_chosen_time()
    {
        using var factory = new PanelFactory();
        _ = factory.Client();
        var backups = factory.Services.GetRequiredService<BackupService>();
        var store = factory.Services.GetRequiredService<PanelSettingsStore>();
        var folder = Path.Combine(backups.Paths.DataDir, "remote");
        store.Save(store.Current with
        {
            Backup = new BackupSettings
            {
                Target = new() { Protocol = BackupProtocol.Local, Folder = folder },
                ScheduleEnabled = true, Time = "03:00", FullBackupDay = DayOfWeek.Sunday,
            },
        });

        await backups.RunScheduledAsync(new DateTime(2026, 10, 19, 2, 59, 0), default); // Monday, too early
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any());

        await backups.RunScheduledAsync(new DateTime(2026, 10, 19, 3, 0, 30), default);
        await backups.RunScheduledAsync(new DateTime(2026, 10, 19, 9, 0, 0), default); // same day: nothing
        Assert.Single(Directory.GetFiles(folder));

        await backups.RunScheduledAsync(new DateTime(2026, 10, 20, 3, 1, 0), default); // Tuesday: incremental
        Assert.Equal(2, Directory.GetFiles(folder).Length);
        Assert.Contains(Directory.GetFiles(folder), f => f.Contains("_incr_"));
    }

    private static async Task AddUserAsync(IServiceProvider services, string email)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.CreateAsync(new ApplicationUser { UserName = email, Email = email }, "Test-Password-123");
        Assert.True(result.Succeeded);
    }

    private static int CountUsers(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AspNetUsers";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
