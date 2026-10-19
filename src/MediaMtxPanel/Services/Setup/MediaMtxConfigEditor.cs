using System.Text.RegularExpressions;
using MediaMtxPanel.Data;

namespace MediaMtxPanel.Services.Setup;

/// <summary>
/// Changes top-level settings in a mediamtx.yml without touching the rest of the file (comments, paths,
/// other settings). MediaMTX writes every top-level key at column 0, and a key's value either sits on the
/// same line or continues on the following lines that are indented or start with "- ".
/// </summary>
public static partial class MediaMtxConfigEditor
{
    /// <summary>Sets <paramref name="key"/> to the YAML value <paramref name="value"/>; appends the key when missing.</summary>
    public static string Set(string yaml, string key, string value)
    {
        var newline = yaml.Contains("\r\n") ? "\r\n" : "\n";
        var lines = yaml.Split(newline).ToList();
        var line = $"{key}: {value}";

        var start = lines.FindIndex(l => IsKeyLine(l, key));
        if (start < 0)
        {
            // Append before the trailing empty line(s) so the file keeps ending with a newline.
            var at = lines.Count;
            while (at > 0 && lines[at - 1].Length == 0)
                at--;
            lines.Insert(at, line);
            return string.Join(newline, lines);
        }

        var end = start + 1;
        var lastContent = start;
        while (end < lines.Count && IsContinuation(lines[end]))
        {
            if (lines[end].Trim().Length > 0)
                lastContent = end;
            end++;
        }
        // Keep blank lines that separate this key from the next one.
        lines.RemoveRange(start, lastContent - start + 1);
        lines.Insert(start, line);
        return string.Join(newline, lines);
    }

    /// <summary>Returns the value of a top-level key when it fits on the key's own line, otherwise null.</summary>
    public static string? Get(string yaml, string key)
    {
        foreach (var l in yaml.Split('\n'))
        {
            if (IsKeyLine(l, key))
                return StripComment(l[(key.Length + 1)..]).Trim();
        }
        return null;
    }

    /// <summary>
    /// Applies the settings the panel needs: authentication through the panel's hook, the Control API on
    /// <paramref name="apiPort"/> and the playback server (recordings) on <paramref name="playbackPort"/>.
    /// Adds <paramref name="publicHost"/> to webrtcAdditionalHosts when that list is empty.
    /// </summary>
    public static string ConfigureForPanel(string yaml, string hookUrl, int apiPort, string? publicHost,
        int playbackPort = MediaServer.DefaultPlaybackPort)
    {
        yaml = Set(yaml, "authMethod", "http");
        yaml = Set(yaml, "authHTTPAddress", Quote(hookUrl));
        yaml = Set(yaml, "authHTTPExclude", "[]");
        yaml = Set(yaml, "api", "yes");
        yaml = Set(yaml, "apiAddress", $":{apiPort}");
        yaml = Set(yaml, "playback", "yes");
        yaml = Set(yaml, "playbackAddress", $":{playbackPort}");

        var hosts = Get(yaml, "webrtcAdditionalHosts");
        if (!string.IsNullOrWhiteSpace(publicHost) && (hosts is null || hosts == "[]"))
            yaml = Set(yaml, "webrtcAdditionalHosts", $"[{Quote(publicHost.Trim())}]");
        return yaml;
    }

    /// <summary>The snippet shown for manual setup; the same settings <see cref="ConfigureForPanel"/> writes.</summary>
    public static string ManualSnippet(string hookUrl, int apiPort, string? publicHost,
        int playbackPort = MediaServer.DefaultPlaybackPort) => $"""
        authMethod: http
        authHTTPAddress: {Quote(hookUrl)}
        authHTTPExclude: []

        api: yes
        apiAddress: :{apiPort}

        playback: yes
        playbackAddress: :{playbackPort}

        webrtcAdditionalHosts: [{Quote(string.IsNullOrWhiteSpace(publicHost) ? "<public address>" : publicHost.Trim())}]
        """;

    private static bool IsKeyLine(string line, string key) =>
        line.StartsWith(key, StringComparison.Ordinal) && line.Length > key.Length
        && line[key.Length] == ':' && (line.Length == key.Length + 1 || char.IsWhiteSpace(line[key.Length + 1]));

    private static bool IsContinuation(string line) =>
        line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('-');

    private static string StripComment(string value) => CommentRegex().Replace(value, "");

    /// <summary>Single-quotes a scalar when YAML would otherwise misread it (e.g. '?', '#', ':').</summary>
    private static string Quote(string value) =>
        SafeScalarRegex().IsMatch(value) ? value : "'" + value.Replace("'", "''") + "'";

    [GeneratedRegex(@"\s+#.*$")]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._/-]+$")]
    private static partial Regex SafeScalarRegex();
}
