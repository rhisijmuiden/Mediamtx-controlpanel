using System.Net.Security;
using System.Text;
using FluentFTP;
using Renci.SshNet;

namespace MediaMtxPanel.Services.Backup;

public sealed record RemoteBackupFile(string Name, long Size, DateTimeOffset? Modified);

/// <summary>Login details with the secrets already decrypted; only lives for one operation.</summary>
public sealed record BackupLogin(BackupTarget Target, string? Password, string? PrivateKey, string? KeyPassphrase);

/// <summary>A remote folder that holds backup archives. Uploads go to a temporary name first, so a
/// half-written archive never looks like a backup.</summary>
public interface IBackupStorage : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    Task<IReadOnlyList<RemoteBackupFile>> ListAsync(CancellationToken ct);
    Task UploadAsync(string localPath, string name, CancellationToken ct);
    Task DownloadAsync(string name, string localPath, CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
}

public sealed class BackupHostKeyException(string message) : Exception(message);

public static class BackupStorage
{
    /// <param name="hostKeySeen">SCP/SFTP: called with the server's fingerprint when none was trusted yet.</param>
    public static IBackupStorage Create(BackupLogin login, Action<string>? hostKeySeen = null) => login.Target.Protocol switch
    {
        BackupProtocol.Local => new LocalBackupStorage(login.Target.Folder),
        BackupProtocol.Scp or BackupProtocol.Sftp => new SshBackupStorage(login, hostKeySeen),
        _ => new FtpBackupStorage(login),
    };

    internal const string PartSuffix = ".part";

    internal static bool IsBackupName(string name) => BackupArchive.TryParseName(name, out _);
}

internal sealed class LocalBackupStorage(string folder) : IBackupStorage
{
    public Task ConnectAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteBackupFile>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RemoteBackupFile>>(new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => BackupStorage.IsBackupName(f.Name))
            .Select(f => new RemoteBackupFile(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList());

    public async Task UploadAsync(string localPath, string name, CancellationToken ct)
    {
        var part = Path.Combine(folder, name + BackupStorage.PartSuffix);
        await using (var source = File.OpenRead(localPath))
        await using (var target = File.Create(part))
            await source.CopyToAsync(target, ct);
        File.Move(part, Path.Combine(folder, name), overwrite: true);
    }

    public async Task DownloadAsync(string name, string localPath, CancellationToken ct)
    {
        await using var source = File.OpenRead(Path.Combine(folder, name));
        await using var target = File.Create(localPath);
        await source.CopyToAsync(target, ct);
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        File.Delete(Path.Combine(folder, name));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>FTP, explicit FTPS (AUTH TLS) and implicit FTPS through FluentFTP.</summary>
internal sealed class FtpBackupStorage : IBackupStorage
{
    private readonly AsyncFtpClient client;
    private readonly string folder;

    public FtpBackupStorage(BackupLogin login)
    {
        var t = login.Target;
        folder = string.IsNullOrWhiteSpace(t.Folder) ? "." : t.Folder.Trim().TrimEnd('/');
        var config = new FtpConfig
        {
            EncryptionMode = t.Protocol switch
            {
                BackupProtocol.Ftps => FtpEncryptionMode.Explicit,
                BackupProtocol.FtpsImplicit => FtpEncryptionMode.Implicit,
                _ => FtpEncryptionMode.None,
            },
            // Only TLS 1.2 and 1.3.
            SslProtocols = System.Security.Authentication.SslProtocols.None,
            ConnectTimeout = 20_000,
            ReadTimeout = 60_000,
            DataConnectionConnectTimeout = 20_000,
            DataConnectionReadTimeout = 60_000,
            RetryAttempts = 2,
        };
        client = new AsyncFtpClient(t.Host.Trim(), login.Target.UserName ?? "anonymous", login.Password ?? "", t.Port, config);
        client.ValidateCertificate += (_, e) => e.Accept = e.PolicyErrors == SslPolicyErrors.None || t.AllowInvalidCertificate;
    }

    private string PathOf(string name) => folder == "." ? name : $"{folder}/{name}";

    public async Task ConnectAsync(CancellationToken ct)
    {
        await client.Connect(ct);
        if (folder != "." && !await client.DirectoryExists(folder, ct))
            await client.CreateDirectory(folder, true, ct);
    }

    public async Task<IReadOnlyList<RemoteBackupFile>> ListAsync(CancellationToken ct) =>
        (await client.GetListing(folder, ct))
            .Where(i => i.Type == FtpObjectType.File && BackupStorage.IsBackupName(i.Name))
            .Select(i => new RemoteBackupFile(i.Name, i.Size, i.Modified == DateTime.MinValue ? null : new DateTimeOffset(i.Modified, TimeSpan.Zero)))
            .ToList();

    public async Task UploadAsync(string localPath, string name, CancellationToken ct)
    {
        var part = PathOf(name + BackupStorage.PartSuffix);
        var status = await client.UploadFile(localPath, part, FtpRemoteExists.Overwrite, true, FtpVerify.None, null, ct);
        if (status == FtpStatus.Failed)
            throw new IOException($"Upload of {name} failed.");
        if (await client.FileExists(PathOf(name), ct))
            await client.DeleteFile(PathOf(name), ct);
        await client.Rename(part, PathOf(name), ct);
    }

    public async Task DownloadAsync(string name, string localPath, CancellationToken ct)
    {
        var status = await client.DownloadFile(localPath, PathOf(name), FtpLocalExists.Overwrite, FtpVerify.None, null, ct);
        if (status == FtpStatus.Failed)
            throw new IOException($"Download of {name} failed.");
    }

    public Task DeleteAsync(string name, CancellationToken ct) => client.DeleteFile(PathOf(name), ct);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client.IsConnected)
                await client.Disconnect();
        }
        catch (Exception) { /* closing anyway */ }
        await client.DisposeAsync();
    }
}

/// <summary>SCP and SFTP through SSH.NET. SCP itself only copies files, so listing, renaming and
/// deleting run as shell commands over the same SSH login.</summary>
internal sealed class SshBackupStorage(BackupLogin login, Action<string>? hostKeySeen) : IBackupStorage
{
    private readonly BackupTarget target = login.Target;
    private readonly string folder = string.IsNullOrWhiteSpace(login.Target.Folder) ? "." : login.Target.Folder.Trim().TrimEnd('/');
    private SshClient? shell;
    private ScpClient? scp;
    private SftpClient? sftp;
    private string? rejectedKey;

    private string PathOf(string name) => folder == "." ? name : $"{folder}/{name}";

    public async Task ConnectAsync(CancellationToken ct)
    {
        try
        {
            await Task.Run(ConnectCore, ct);
        }
        catch (Exception) when (rejectedKey is not null)
        {
            throw new BackupHostKeyException(rejectedKey);
        }
    }

    private void ConnectCore()
    {
        var info = Connection();
        if (target.Protocol == BackupProtocol.Sftp)
        {
            sftp = new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(2) };
            Trust(sftp);
            sftp.Connect();
            CreateSftpFolder();
        }
        else
        {
            shell = new SshClient(info);
            Trust(shell);
            shell.Connect();
            Run($"mkdir -p -- {Quote(folder)}");
            scp = new ScpClient(info, RemotePathTransformation.ShellQuote) { OperationTimeout = TimeSpan.FromMinutes(5) };
            Trust(scp);
            scp.Connect();
        }
    }

    public Task<IReadOnlyList<RemoteBackupFile>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<RemoteBackupFile>>(() =>
    {
        if (sftp is not null)
            return sftp.ListDirectory(folder)
                .Where(f => f.IsRegularFile && BackupStorage.IsBackupName(f.Name))
                .Select(f => new RemoteBackupFile(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
                .ToList();

        // Name, size and modification time per file, tab separated; works with GNU and BusyBox tools.
        var output = Run($"cd -- {Quote(folder)} && for f in *.tar; do [ -f \"$f\" ] && printf '%s\\t%s\\t%s\\n' \"$f\" \"$(wc -c < \"$f\")\" \"$(date -r \"$f\" +%s 2>/dev/null || echo 0)\"; done; true");
        var files = new List<RemoteBackupFile>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length != 3 || !BackupStorage.IsBackupName(parts[0]))
                continue;
            long.TryParse(parts[1].Trim(), out var size);
            DateTimeOffset? modified = long.TryParse(parts[2].Trim(), out var unix) && unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
            files.Add(new(parts[0], size, modified));
        }
        return files;
    }, ct);

    public Task UploadAsync(string localPath, string name, CancellationToken ct) => Task.Run(() =>
    {
        using var source = File.OpenRead(localPath);
        var part = PathOf(name + BackupStorage.PartSuffix);
        if (sftp is not null)
        {
            sftp.UploadFile(source, part, true);
            sftp.RenameFile(part, PathOf(name), true);
        }
        else
        {
            scp!.Upload(source, part);
            Run($"mv -f -- {Quote(part)} {Quote(PathOf(name))}");
        }
    }, ct);

    public Task DownloadAsync(string name, string localPath, CancellationToken ct) => Task.Run(() =>
    {
        using var target = File.Create(localPath);
        if (sftp is not null)
            sftp.DownloadFile(PathOf(name), target);
        else
            scp!.Download(PathOf(name), target);
    }, ct);

    public Task DeleteAsync(string name, CancellationToken ct) => Task.Run(() =>
    {
        if (sftp is not null)
            sftp.DeleteFile(PathOf(name));
        else
            Run($"rm -f -- {Quote(PathOf(name))}");
    }, ct);

    public ValueTask DisposeAsync()
    {
        foreach (var client in new BaseClient?[] { sftp, scp, shell })
        {
            if (client is null) continue;
            try { if (client.IsConnected) client.Disconnect(); } catch (Exception) { /* closing anyway */ }
            client.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private void CreateSftpFolder()
    {
        if (folder == ".") return;
        var path = folder.StartsWith('/') ? "" : null;
        foreach (var part in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path is null ? part : $"{path}/{part}";
            if (!sftp!.Exists(path))
                sftp.CreateDirectory(path);
        }
    }

    private string Run(string command)
    {
        using var cmd = shell!.RunCommand(command);
        if (cmd.ExitStatus is not 0 and not null)
            throw new IOException($"'{command.Split(' ')[0]}' failed on the server: {cmd.Error.Trim()}");
        return cmd.Result;
    }

    /// <summary>POSIX single quoting: 'it'\''s'.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private void Trust(BaseClient client) => client.HostKeyReceived += (_, e) =>
    {
        var fingerprint = $"SHA256:{e.FingerPrintSHA256}";
        if (string.IsNullOrEmpty(target.HostKeyFingerprint))
        {
            hostKeySeen?.Invoke(fingerprint);
            e.CanTrust = true;
        }
        else
        {
            e.CanTrust = string.Equals(target.HostKeyFingerprint, fingerprint, StringComparison.Ordinal);
            if (!e.CanTrust)
                rejectedKey = fingerprint;
        }
    };

    private Renci.SshNet.ConnectionInfo Connection()
    {
        var user = string.IsNullOrWhiteSpace(target.UserName) ? "root" : target.UserName.Trim();
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(login.PrivateKey))
        {
            var key = login.PrivateKey.Replace("\r\n", "\n").Trim() + "\n";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(key));
            var file = string.IsNullOrEmpty(login.KeyPassphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, login.KeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(user, file));
        }
        if (!string.IsNullOrEmpty(login.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(user, login.Password));
            var interactive = new KeyboardInteractiveAuthenticationMethod(user);
            var password = login.Password;
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    prompt.Response = password;
            };
            methods.Add(interactive);
        }
        if (methods.Count == 0)
            methods.Add(new NoneAuthenticationMethod(user));
        return new Renci.SshNet.ConnectionInfo(target.Host.Trim(), target.Port, user, [.. methods]) { Timeout = TimeSpan.FromSeconds(20) };
    }
}
