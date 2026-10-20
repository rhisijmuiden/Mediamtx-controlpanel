using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MediaMtxPanel.Services.Backup;

public enum BackupKind { Full, Incremental }

/// <summary>
/// File name of an archive: <c>mediamtx-panel_20261019-030000_full.tar</c>, or for an incremental backup
/// <c>mediamtx-panel_20261020-030000_incr_20261019-030000.tar</c>, where the last part names the full
/// backup it builds on. The remote listing alone is enough to see which archives belong together.
/// </summary>
public sealed partial record BackupName(DateTime Created, BackupKind Kind, DateTime FullCreated)
{
    private const string Stamp = "yyyyMMdd-HHmmss";

    public static BackupName Full(DateTime createdUtc)
    {
        var t = Trim(createdUtc);
        return new(t, BackupKind.Full, t);
    }

    public static BackupName Incremental(DateTime createdUtc, DateTime fullCreatedUtc) =>
        new(Trim(createdUtc), BackupKind.Incremental, Trim(fullCreatedUtc));

    public override string ToString() => Kind == BackupKind.Full
        ? $"mediamtx-panel_{Created.ToString(Stamp, CultureInfo.InvariantCulture)}_full.tar"
        : $"mediamtx-panel_{Created.ToString(Stamp, CultureInfo.InvariantCulture)}_incr_{FullCreated.ToString(Stamp, CultureInfo.InvariantCulture)}.tar";

    /// <summary>The name of the full backup this archive belongs to (itself for a full backup).</summary>
    public string FullName => Full(FullCreated).ToString();

    public static bool TryParse(string name, out BackupName result)
    {
        result = null!;
        var m = Pattern().Match(name);
        if (!m.Success)
            return false;
        var created = Parse(m.Groups["t"].Value);
        if (m.Groups["kind"].Value == "full")
        {
            result = new(created, BackupKind.Full, created);
            return true;
        }
        if (!m.Groups["base"].Success)
            return false;
        result = new(created, BackupKind.Incremental, Parse(m.Groups["base"].Value));
        return true;
    }

    private static DateTime Parse(string s) =>
        DateTime.SpecifyKind(DateTime.ParseExact(s, Stamp, CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static DateTime Trim(DateTime t) =>
        DateTime.SpecifyKind(new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    [GeneratedRegex(@"^mediamtx-panel_(?<t>\d{8}-\d{6})_(?<kind>full|incr)(?:_(?<base>\d{8}-\d{6}))?\.tar$")]
    private static partial Regex Pattern();
}

public sealed record BackupFileEntry(string Path, long Size, string Sha256, List<string> Chunks);

/// <summary>What one archive contains. A file is a list of chunks; an incremental archive only holds the
/// chunks that no earlier archive of its full backup holds, so a restore needs the whole chain.</summary>
public sealed record BackupManifest
{
    public int Format { get; init; } = 1;
    public string Name { get; init; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter<BackupKind>))]
    public BackupKind Kind { get; init; }

    public DateTimeOffset Created { get; init; }
    public string AppVersion { get; init; } = "";

    /// <summary>Archives this one builds on, oldest (the full backup) first. Empty for a full backup.</summary>
    public List<string> Base { get; init; } = [];

    public List<BackupFileEntry> Files { get; init; } = [];
}

public sealed class BackupPassphraseException(string message) : Exception(message);

/// <summary>
/// Reads and writes backup archives: a tar file with
/// <list type="bullet">
/// <item><c>backup.json</c>: format and, when encrypted, the key derivation parameters (never secret);</item>
/// <item><c>manifest.json</c>: the <see cref="BackupManifest"/>;</item>
/// <item><c>chunks/&lt;sha256&gt;</c>: gzip-compressed pieces of the files, at most <see cref="ChunkSize"/> each.</item>
/// </list>
/// With a passphrase, the manifest and every chunk are encrypted with AES-256-GCM (key from PBKDF2-SHA256,
/// a fresh salt per archive, the entry name as associated data), so nothing but sizes can be read without it.
/// </summary>
public static class BackupArchive
{
    public const int ChunkSize = 64 * 1024;
    private const int Iterations = 600_000;
    private const string HeaderEntry = "backup.json";
    private const string ManifestEntry = "manifest.json";
    private const string ChunkPrefix = "chunks/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Header(int Format, bool Encrypted, string? Kdf, int Iterations, string? Salt);

    public static bool TryParseName(string name, out BackupName parsed) => BackupName.TryParse(name, out parsed);

    /// <summary>Splits a file into chunks: (sha256 of the chunk, its bytes).</summary>
    public static IEnumerable<(string Hash, byte[] Data)> ReadChunks(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[ChunkSize];
        while (true)
        {
            var read = stream.ReadAtLeast(buffer, ChunkSize, throwOnEndOfStream: false);
            if (read == 0)
                yield break;
            var data = buffer.AsSpan(0, read).ToArray();
            yield return (Convert.ToHexStringLower(SHA256.HashData(data)), data);
            if (read < ChunkSize)
                yield break;
        }
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Writes an archive with the manifest and the given chunks (hash → uncompressed bytes).</summary>
    public static void Write(string path, BackupManifest manifest, IEnumerable<KeyValuePair<string, byte[]>> chunks, string? passphrase)
    {
        byte[]? key = null;
        Header header;
        if (string.IsNullOrEmpty(passphrase))
            header = new(1, false, null, 0, null);
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            key = DeriveKey(passphrase, salt, Iterations);
            header = new(1, true, "PBKDF2-SHA256", Iterations, Convert.ToBase64String(salt));
        }

        using var file = File.Create(path);
        using var tar = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: false);
        Add(tar, HeaderEntry, JsonSerializer.SerializeToUtf8Bytes(header, Json));
        Add(tar, ManifestEntry, Seal(JsonSerializer.SerializeToUtf8Bytes(manifest, Json), ManifestEntry, key));
        foreach (var (hash, data) in chunks)
        {
            var name = ChunkPrefix + hash;
            Add(tar, name, Seal(Compress(data), name, key));
        }
    }

    /// <summary>Reads only the manifest (the header and manifest are the first entries).</summary>
    public static BackupManifest ReadManifest(string path, string? passphrase) =>
        Read(path, passphrase, chunkFolder: null);

    /// <summary>Reads the manifest and writes every chunk, uncompressed and verified, to <paramref name="chunkFolder"/>.</summary>
    public static BackupManifest Extract(string path, string? passphrase, string chunkFolder) =>
        Read(path, passphrase, chunkFolder);

    private static BackupManifest Read(string path, string? passphrase, string? chunkFolder)
    {
        using var file = File.OpenRead(path);
        using var tar = new TarReader(file);
        Header? header = null;
        byte[]? key = null;
        BackupManifest? manifest = null;
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null)
                continue;
            using var ms = new MemoryStream();
            entry.DataStream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (entry.Name == HeaderEntry)
            {
                header = JsonSerializer.Deserialize<Header>(bytes, Json) ?? throw new InvalidDataException("Empty backup header.");
                if (header.Format != 1)
                    throw new InvalidDataException($"Backup format {header.Format} is not supported by this version of the panel.");
                if (header.Encrypted)
                {
                    if (string.IsNullOrEmpty(passphrase))
                        throw new BackupPassphraseException("This backup is encrypted; enter its passphrase.");
                    key = DeriveKey(passphrase, Convert.FromBase64String(header.Salt!), header.Iterations);
                }
            }
            else if (entry.Name == ManifestEntry)
            {
                if (header is null)
                    throw new InvalidDataException("The backup has no header.");
                byte[] plain;
                try
                {
                    plain = Open(bytes, ManifestEntry, key);
                }
                catch (CryptographicException)
                {
                    throw new BackupPassphraseException("The passphrase does not match this backup.");
                }
                manifest = JsonSerializer.Deserialize<BackupManifest>(plain, Json) ?? throw new InvalidDataException("Empty manifest.");
                if (chunkFolder is null)
                    return manifest;
            }
            else if (entry.Name.StartsWith(ChunkPrefix, StringComparison.Ordinal) && chunkFolder is not null && manifest is not null)
            {
                var hash = entry.Name[ChunkPrefix.Length..];
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException($"Unexpected entry {entry.Name}.");
                var data = Decompress(Open(bytes, entry.Name, key));
                if (Convert.ToHexStringLower(SHA256.HashData(data)) != hash)
                    throw new InvalidDataException($"Chunk {hash} is damaged.");
                File.WriteAllBytes(Path.Combine(chunkFolder, hash), data);
            }
        }
        return manifest ?? throw new InvalidDataException("The archive has no manifest; it is not a panel backup.");
    }

    private static void Add(TarWriter tar, string name, byte[] data)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(data),
            ModificationTime = DateTimeOffset.UnixEpoch,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        tar.WriteEntry(entry);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>nonce (12) | ciphertext | tag (16); unchanged without a key.</summary>
    private static byte[] Seal(byte[] plain, string name, byte[]? key)
    {
        if (key is null)
            return plain;
        var output = new byte[12 + plain.Length + 16];
        var nonce = output.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, output.AsSpan(12, plain.Length), output.AsSpan(12 + plain.Length), Encoding.UTF8.GetBytes(name));
        return output;
    }

    private static byte[] Open(byte[] sealedData, string name, byte[]? key)
    {
        if (key is null)
            return sealedData;
        if (sealedData.Length < 28)
            throw new CryptographicException("Truncated entry.");
        var plain = new byte[sealedData.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(12, plain.Length), sealedData.AsSpan(12 + plain.Length), plain, Encoding.UTF8.GetBytes(name));
        return plain;
    }

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data);
        return ms.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }
}
