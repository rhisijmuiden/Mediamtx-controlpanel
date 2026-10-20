using System.Text.Json.Serialization;

namespace MediaMtxPanel.Services.Backup;

public enum BackupProtocol
{
    /// <summary>Plain FTP; user name, password and data travel unencrypted.</summary>
    Ftp,
    /// <summary>FTP upgraded with AUTH TLS (explicit FTPS, usually port 21).</summary>
    Ftps,
    /// <summary>TLS from the first byte (implicit FTPS, usually port 990).</summary>
    FtpsImplicit,
    /// <summary>SCP over SSH (port 22); needs a POSIX shell on the server for listing and deleting.</summary>
    Scp,
    /// <summary>SFTP over SSH (port 22).</summary>
    Sftp,
    /// <summary>A folder on this machine, for example a mounted NAS share.</summary>
    Local,
}

/// <summary>Where backups go and how they get there. Secrets are encrypted with ASP.NET Data Protection.</summary>
public record BackupTarget
{
    [JsonConverter(typeof(JsonStringEnumConverter<BackupProtocol>))]
    public BackupProtocol Protocol { get; init; } = BackupProtocol.Sftp;

    public string Host { get; init; } = "";
    public int Port { get; init; } = 22;
    public string? UserName { get; init; }
    public string? PasswordProtected { get; init; }

    /// <summary>OpenSSH private key (SCP/SFTP), encrypted.</summary>
    public string? PrivateKeyProtected { get; init; }
    public string? KeyPassphraseProtected { get; init; }

    /// <summary>Folder on the server (or on this machine for <see cref="BackupProtocol.Local"/>).</summary>
    public string Folder { get; init; } = "mediamtx-panel-backups";

    /// <summary>FTPS: accept a self-signed or otherwise invalid certificate.</summary>
    public bool AllowInvalidCertificate { get; init; }

    /// <summary>SCP/SFTP: SHA256 fingerprint of the server's host key, trusted on first connection.</summary>
    public string? HostKeyFingerprint { get; init; }

    public bool IsSsh => Protocol is BackupProtocol.Scp or BackupProtocol.Sftp;
    public bool IsFtp => Protocol is BackupProtocol.Ftp or BackupProtocol.Ftps or BackupProtocol.FtpsImplicit;

    public static int DefaultPort(BackupProtocol protocol) => protocol switch
    {
        BackupProtocol.Ftp or BackupProtocol.Ftps => 21,
        BackupProtocol.FtpsImplicit => 990,
        _ => 22,
    };
}

/// <summary>Settings → Backup. Stored in panel-settings.json; a restore keeps the current ones.</summary>
public record BackupSettings
{
    public BackupTarget Target { get; init; } = new();

    /// <summary>Optional passphrase that encrypts the backups (AES-256-GCM), itself stored encrypted.</summary>
    public string? PassphraseProtected { get; init; }

    /// <summary>Run a backup every day at <see cref="Time"/>.</summary>
    public bool ScheduleEnabled { get; init; }

    /// <summary>Local time of day, "HH:mm".</summary>
    public string Time { get; init; } = "03:00";

    /// <summary>The day a full backup is made; on the other days the backup is incremental. Null: full every day.</summary>
    public DayOfWeek? FullBackupDay { get; init; } = DayOfWeek.Sunday;

    /// <summary>How many full backups (each with its incremental backups) stay on the remote location.</summary>
    public int KeepFullBackups { get; init; } = 4;

    /// <summary>Mail the administrators when a scheduled backup fails (needs Settings → Mail).</summary>
    public bool MailOnFailure { get; init; } = true;

    public bool IsConfigured => Target.Protocol == BackupProtocol.Local
        ? !string.IsNullOrWhiteSpace(Target.Folder)
        : !string.IsNullOrWhiteSpace(Target.Host);

    public TimeOnly TimeOfDay => TimeOnly.TryParse(Time, out var t) ? t : new TimeOnly(3, 0);
}
