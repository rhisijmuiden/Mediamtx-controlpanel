using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.RegularExpressions;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services.Localization;
using Renci.SshNet;
using Renci.SshNet.Common;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace MediaMtxPanel.Services.Setup;

/// <summary>SSH login for a one-off setup run. Never stored.</summary>
public class SshLoginInput
{
    [Required(ErrorMessage = "Enter the SSH host.")]
    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = 22;

    [Required(ErrorMessage = "Enter the SSH user.")]
    public string User { get; set; } = "root";

    /// <summary>Login password; also used for sudo when the user is not root.</summary>
    public string? Password { get; set; }

    /// <summary>Private key in OpenSSH or PEM format, as an alternative to the password.</summary>
    public string? PrivateKey { get; set; }

    public string? KeyPassphrase { get; set; }

    /// <summary>Path of mediamtx.yml on the server; empty means "find it".</summary>
    public string? ConfigPath { get; set; }

    public bool InstallIfMissing { get; set; } = true;

    /// <summary>Address at which the MediaMTX server reaches the panel, e.g. http://10.0.0.10:8080.</summary>
    [Required(ErrorMessage = "Enter the panel address.")]
    [RegularExpression(@"^https?://\S+$", ErrorMessage = "Use an address such as http://10.0.0.10:8080.")]
    public string PanelUrl { get; set; } = "";

    public string HookUrl(string hookSecret) => $"{PanelUrl.Trim().TrimEnd('/')}/api/mediamtx/auth?secret={hookSecret}";

    /// <summary>Removes the secrets from memory once the run is over.</summary>
    public void ForgetSecrets()
    {
        Password = null;
        PrivateKey = null;
        KeyPassphrase = null;
    }
}

public record SshSetupResult(bool Ok, string Message);

/// <summary>
/// Logs in to a MediaMTX server over SSH and points its configuration at the panel: authentication through
/// the panel's hook (with the server's secret) and the Control API on the port of the server's API URL.
/// Optionally installs MediaMTX as a systemd service first. The previous mediamtx.yml is kept as a backup,
/// and restored when MediaMTX does not start with the new one.
/// </summary>
public class SshServerSetup(ILogger<SshServerSetup> logger, TextLocalizer L)
{
    /// <summary>Installed when the server has no MediaMTX yet; same version as the panel's own installer.</summary>
    public const string MediaMtxVersion = "1.21.2";

    private enum Elevation { Root, Sudo, SudoWithPassword }

    private sealed record Remote(SshClient Client, Elevation Elevation, string? SudoPassword);

    private sealed record CommandResult(int Exit, string Output, string Error);

    public async Task<SshSetupResult> ConfigureAsync(MediaServer server, SshLoginInput login, string hookUrl,
        Action<string> log, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => ConfigureCore(server, login, hookUrl, log, ct), ct);
        }
        catch (SshAuthenticationException)
        {
            return new(false, L["The SSH server refused the login for '{0}'. Check the user, password or key.", login.User]);
        }
        catch (SshConnectionException ex)
        {
            return new(false, L["The SSH connection to {0} failed: {1}", $"{login.Host}:{login.Port}", ex.Message]);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or SshOperationTimeoutException or TimeoutException)
        {
            return new(false, L["Cannot reach {0} over SSH: {1}", $"{login.Host}:{login.Port}", ex.Message]);
        }
        catch (SshException ex) when (ex.Message.Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, L["The private key cannot be read: {0}", ex.Message]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SSH setup of {Host} failed", login.Host);
            return new(false, L["SSH setup failed: {0}", ex.Message]);
        }
    }

    private SshSetupResult ConfigureCore(MediaServer server, SshLoginInput login, string hookUrl, Action<string> log, CancellationToken ct)
    {
        var apiPort = new Uri(server.ApiUrl).Port;
        var playbackPort = new Uri(server.PlaybackBaseUrl()).Port;

        using var client = new SshClient(Connection(login));
        client.HostKeyReceived += (_, e) =>
        {
            log($"Host key of {login.Host}: {e.HostKeyName} SHA256:{e.FingerPrintSHA256}");
            e.CanTrust = true;
        };
        log($"Connecting to {login.User}@{login.Host}:{login.Port}…");
        client.Connect();
        log("Logged in.");

        var remote = DetectElevation(client, login, log);
        if (remote is null)
            return new(false, L["'{0}' is not root and cannot use sudo. Log in as root, or as a user with sudo rights (and fill in the password).", login.User]);

        ct.ThrowIfCancellationRequested();
        log("Looking for MediaMTX…");
        var found = Inspect(remote, login.ConfigPath, hookUrl);

        if (found.Config is null && found.Binary is null && found.Restart == "none")
        {
            if (!login.InstallIfMissing)
                return new(false, L["MediaMTX was not found on the server. Turn on the option to install it, or install it yourself."]);
            log($"MediaMTX is not installed. Installing MediaMTX v{MediaMtxVersion}…");
            var install = Run(remote, InstallScript, [MediaMtxVersion]);
            LogLines(log, install.Output);
            if (install.Exit != 0)
                return new(false, L["Installing MediaMTX failed: {0}", FirstLine(install.Error, install.Output)]);
            found = Inspect(remote, login.ConfigPath, hookUrl);
        }

        foreach (var (key, value) in found.Facts.Where(f => f.Key is "os" or "version" or "binary" or "restart"))
            log($"  {key}: {value}");

        if (found.Config is null)
        {
            if (found.Restart.StartsWith("docker:"))
                return new(false, L["MediaMTX runs in Docker container '{0}', but its mediamtx.yml is not mounted from the host, so the panel cannot change it. Mount a mediamtx.yml into the container as described under manual setup.", found.Restart[7..]]);
            return new(false, L["MediaMTX was found, but not its mediamtx.yml. Enter the path of the configuration file under Advanced and try again."]);
        }
        log($"  config: {found.ConfigPath}");

        if (found.Version is { } version && version < new Version(1, 16))
            return new(false, L["MediaMTX {0} is too old: the panel needs v1.16 or newer. Update MediaMTX first.", found.Facts["version"]]);

        if (found.Facts.TryGetValue("dockerenv", out var env) && env.Length > 0)
            log($"  Warning: the container sets {env}; these environment variables override mediamtx.yml.");

        // The server must reach the panel's auth hook, or MediaMTX would refuse every request (the panel's included).
        switch (found.Facts.GetValueOrDefault("hook"))
        {
            case null or "" or "none":
                log("  curl and wget are missing, so the panel address could not be checked from the server.");
                break;
            case "000" or "fail":
                return new(false, L["The server cannot reach the panel at {0}. Fix the panel address (it must be reachable from this server), or open the panel's port in the firewall, and try again. Nothing was changed on the server.", hookUrl]);
            case "403":
                return new(false, L["The panel did not recognise this server's secret. Reload the page and try again."]);
            default:
                log($"  The server reaches the panel at {hookUrl}.");
                break;
        }

        var updated = MediaMtxConfigEditor.ConfigureForPanel(found.Config, hookUrl, apiPort, server.PublicHost, playbackPort);
        if (updated == found.Config && found.Running)
        {
            log("mediamtx.yml already has the right settings.");
            return new(true, L["MediaMTX was already configured for the panel."]);
        }

        log("Writing mediamtx.yml (the old file is kept as a backup) and restarting MediaMTX…");
        var apply = Run(remote, ApplyScript, [found.ConfigPath!, found.Restart], Encoding.UTF8.GetBytes(updated));
        LogLines(log, apply.Output);
        if (apply.Exit != 0)
        {
            LogLines(log, apply.Error);
            return new(false, apply.Exit == 2
                ? L["MediaMTX did not start with the new configuration, so the previous configuration was restored. See the setup log for details."]
                : L["Writing the configuration failed: {0}", FirstLine(apply.Error, apply.Output)]);
        }
        if (found.Restart == "none")
            log(found.Running
                ? "MediaMTX does not run as a systemd service or Docker container; it reloads mediamtx.yml by itself."
                : "MediaMTX is not running and has no systemd service or Docker container the panel can start. Start it on the server.");
        if (apply.Output.Contains("ufw=active"))
            log($"  Note: the ufw firewall is active. Ports {apiPort} (API) and {playbackPort} (recordings) must be open for the panel.");
        return new(true, L["MediaMTX is configured for the panel."]);
    }

    private static ConnectionInfo Connection(SshLoginInput login)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(login.PrivateKey))
        {
            var key = login.PrivateKey.Replace("\r\n", "\n").Trim() + "\n";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(key));
            var file = string.IsNullOrEmpty(login.KeyPassphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, login.KeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(login.User, file));
        }
        if (!string.IsNullOrEmpty(login.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(login.User, login.Password));
            var interactive = new KeyboardInteractiveAuthenticationMethod(login.User);
            var password = login.Password;
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    prompt.Response = password;
            };
            methods.Add(interactive);
        }
        if (methods.Count == 0)
            methods.Add(new NoneAuthenticationMethod(login.User));
        return new ConnectionInfo(login.Host.Trim(), login.Port, login.User.Trim(), [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private static Remote? DetectElevation(SshClient client, SshLoginInput login, Action<string> log)
    {
        var id = client.RunCommand("id -u");
        if (id.Result.Trim() == "0")
            return new(client, Elevation.Root, null);

        if (client.RunCommand("sudo -n true").ExitStatus == 0)
        {
            log("Using sudo.");
            return new(client, Elevation.Sudo, null);
        }
        if (string.IsNullOrEmpty(login.Password))
            return null;

        var probe = new Remote(client, Elevation.SudoWithPassword, login.Password);
        if (Run(probe, "true", []).Exit != 0)
            return null;
        log("Using sudo.");
        return probe;
    }

    /// <summary>Runs a shell script as root (directly, or through sudo) with optional data on standard input.</summary>
    private static CommandResult Run(Remote remote, string script, string[] args, byte[]? stdin = null)
    {
        // The script travels base64-encoded so that no quoting can break it.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var body = $"sh -c \"$(printf %s {encoded} | base64 -d)\" sh {string.Join(' ', args.Select(ShellQuote))}";
        var text = remote.Elevation switch
        {
            Elevation.Root => body,
            Elevation.Sudo => "sudo -n " + body,
            _ => "sudo -S -p '' " + body,
        };

        using var cmd = remote.Client.CreateCommand(text);
        cmd.CommandTimeout = TimeSpan.FromMinutes(5);
        var running = cmd.BeginExecute();
        using (var input = cmd.CreateInputStream())
        {
            if (remote.Elevation == Elevation.SudoWithPassword)
                input.Write(Encoding.UTF8.GetBytes(remote.SudoPassword + "\n"));
            if (stdin is not null)
                input.Write(stdin);
        }
        var output = cmd.EndExecute(running);
        return new(cmd.ExitStatus ?? -1, output, cmd.Error);
    }

    private sealed class Inspection
    {
        public Dictionary<string, string> Facts { get; } = [];
        public string? Config { get; set; }
        public string? ConfigPath => Facts.GetValueOrDefault("config") is { Length: > 0 } p ? p : null;
        public string? Binary => Facts.GetValueOrDefault("binary") is { Length: > 0 } b ? b : null;
        public string Restart => Facts.GetValueOrDefault("restart") ?? "none";
        public bool Running => Facts.GetValueOrDefault("running") == "yes";

        public Version? Version =>
            Regex.Match(Facts.GetValueOrDefault("version") ?? "", @"(\d+)\.(\d+)\.(\d+)") is { Success: true } m
                ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
                : null;
    }

    private static Inspection Inspect(Remote remote, string? configPath, string hookUrl)
    {
        var r = Run(remote, InspectScript, [configPath?.Trim() ?? "", hookUrl]);
        var result = new Inspection();
        var parts = r.Output.Split("---CONFIG---", 2);
        foreach (var line in parts[0].Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                result.Facts[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        if (parts.Length == 2 && result.ConfigPath is not null)
            result.Config = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Trim()));
        return result;
    }

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static void LogLines(Action<string> log, string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("ufw="))
                log("  " + line);
        }
    }

    private static string FirstLine(params string[] texts) =>
        texts.SelectMany(t => t.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .FirstOrDefault() ?? "unknown error";

    // ------------------------------------------------------------------ remote scripts (POSIX sh, run as root)

    /// <summary>
    /// $1 = mediamtx.yml path given by the admin (may be empty), $2 = the panel's hook URL.
    /// Prints key=value facts, then "---CONFIG---" and the configuration file in base64.
    /// </summary>
    private const string InspectScript = """
        override="$1"; hook="$2"
        export LC_ALL=C
        unit=""; bin=""; conf=""; wd=""; restart="none"; running="no"; version=""; dockerenv=""
        if command -v systemctl >/dev/null 2>&1; then
          unit=$( { systemctl list-units --type=service --all --no-legend --plain 2>/dev/null; systemctl list-unit-files --type=service --no-legend 2>/dev/null; } \
            | awk '{print $1}' | grep -i '^mediamtx' | grep -vi panel | head -n 1)
          if [ -n "$unit" ]; then
            restart="systemd:$unit"
            systemctl is-active --quiet "$unit" && running="yes"
            argv=$(systemctl show -p ExecStart --value "$unit" 2>/dev/null | sed -n 's/.*argv\[\]=\([^;]*\);.*/\1/p' | head -n 1)
            wd=$(systemctl show -p WorkingDirectory --value "$unit" 2>/dev/null)
            first=1
            for a in $argv; do
              if [ "$first" = 1 ]; then bin="$a"; first=0; continue; fi
              case "$a" in *.yml|*.yaml) conf="$a" ;; esac
            done
            case "$conf" in ""|/*) ;; *) conf="${wd:-/}/$conf" ;; esac
          fi
        fi
        if [ -z "$unit" ] && command -v docker >/dev/null 2>&1; then
          c=$(docker ps --format '{{.Names}} {{.Image}}' 2>/dev/null | awk 'tolower($2) ~ /mediamtx/ {print $1; exit}')
          if [ -n "$c" ]; then
            restart="docker:$c"; running="yes"
            conf=$(docker inspect -f '{{range .Mounts}}{{.Source}}={{.Destination}}{{println}}{{end}}' "$c" 2>/dev/null | awk -F= '$2 ~ /mediamtx\.ya?ml$/ {print $1; exit}')
            version=$(docker exec "$c" /mediamtx --version 2>/dev/null | head -n 1)
            dockerenv=$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$c" 2>/dev/null | grep -E '^MTX_(AUTH|API)' | cut -d= -f1 | tr '\n' ' ')
          fi
        fi
        if [ -z "$bin" ]; then bin=$(command -v mediamtx 2>/dev/null); fi
        for f in /usr/local/bin/mediamtx /opt/mediamtx/mediamtx /usr/bin/mediamtx; do
          if [ -z "$bin" ] && [ -x "$f" ]; then bin="$f"; fi
        done
        if [ -n "$bin" ] && [ -x "$bin" ] && [ -z "$version" ]; then version=$("$bin" --version 2>/dev/null | head -n 1); fi
        if [ "$running" = "no" ] && command -v pgrep >/dev/null 2>&1 && pgrep -x mediamtx >/dev/null 2>&1; then running="yes"; fi
        if [ -n "$override" ]; then
          conf="$override"
        elif [ -z "$conf" ] && [ "${restart%%:*}" != "docker" ]; then
          bindir=""; [ -n "$bin" ] && bindir=$(dirname "$bin")
          for f in "${wd:+$wd/mediamtx.yml}" "${bindir:+$bindir/mediamtx.yml}" /usr/local/etc/mediamtx.yml /usr/etc/mediamtx.yml /etc/mediamtx/mediamtx.yml; do
            if [ -n "$f" ] && [ -f "$f" ]; then conf="$f"; break; fi
          done
        fi
        [ -n "$conf" ] && [ ! -f "$conf" ] && conf=""
        hookcode="none"
        if command -v curl >/dev/null 2>&1; then
          hookcode=$(curl -s -o /dev/null -m 8 -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{}' "$hook" 2>/dev/null)
          [ -z "$hookcode" ] && hookcode="000"
        elif command -v wget >/dev/null 2>&1; then
          wget -q -O /dev/null -T 8 -t 1 --header='Content-Type: application/json' --post-data='{}' "$hook" 2>/dev/null
          case $? in 0|8) hookcode="ok" ;; *) hookcode="fail" ;; esac
        fi
        echo "os=$( (. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME") || uname -sr)"
        echo "binary=$bin"
        echo "version=$version"
        echo "restart=$restart"
        echo "running=$running"
        echo "config=$conf"
        echo "dockerenv=$dockerenv"
        echo "hook=$hookcode"
        if [ -n "$conf" ]; then echo "---CONFIG---"; base64 < "$conf"; fi
        exit 0
        """;

    /// <summary>$1 = MediaMTX version. Installs the binary, a default mediamtx.yml and mediamtx.service.</summary>
    private const string InstallScript = """
        set -e
        export LC_ALL=C
        case "$(uname -m)" in
          x86_64) arch=amd64 ;;
          aarch64|arm64) arch=arm64 ;;
          armv7l) arch=armv7 ;;
          armv6l) arch=armv6 ;;
          *) echo "Unsupported CPU architecture: $(uname -m)" >&2; exit 1 ;;
        esac
        if ! command -v systemctl >/dev/null 2>&1; then echo "This server has no systemd; install MediaMTX yourself." >&2; exit 1; fi
        url="https://github.com/bluenviron/mediamtx/releases/download/v$1/mediamtx_v$1_linux_$arch.tar.gz"
        tmp=$(mktemp -d)
        trap 'rm -rf "$tmp"' EXIT
        echo "Downloading $url"
        if command -v curl >/dev/null 2>&1; then curl -fsSL "$url" -o "$tmp/mediamtx.tar.gz"
        elif command -v wget >/dev/null 2>&1; then wget -q -O "$tmp/mediamtx.tar.gz" "$url"
        else echo "Neither curl nor wget is installed on the server." >&2; exit 1; fi
        tar -xzf "$tmp/mediamtx.tar.gz" -C "$tmp"
        install -m 0755 "$tmp/mediamtx" /usr/local/bin/mediamtx
        id -u mediamtx >/dev/null 2>&1 || useradd --system --no-create-home --home-dir /var/lib/mediamtx --shell /usr/sbin/nologin mediamtx
        mkdir -p /usr/local/etc /var/lib/mediamtx
        chown mediamtx:mediamtx /var/lib/mediamtx
        if [ ! -f /usr/local/etc/mediamtx.yml ]; then
          install -m 0640 -o root -g mediamtx "$tmp/mediamtx.yml" /usr/local/etc/mediamtx.yml
        fi
        cat > /etc/systemd/system/mediamtx.service <<'UNIT'
        [Unit]
        Description=MediaMTX media server
        After=network-online.target
        Wants=network-online.target

        [Service]
        User=mediamtx
        Group=mediamtx
        WorkingDirectory=/var/lib/mediamtx
        ExecStart=/usr/local/bin/mediamtx /usr/local/etc/mediamtx.yml
        Restart=always
        RestartSec=3
        AmbientCapabilities=CAP_NET_BIND_SERVICE

        [Install]
        WantedBy=multi-user.target
        UNIT
        systemctl daemon-reload
        systemctl enable mediamtx.service >/dev/null 2>&1
        echo "Installed MediaMTX v$1 as mediamtx.service."
        """;

    /// <summary>
    /// $1 = mediamtx.yml path, $2 = how to restart ("systemd:unit", "docker:container" or "none"); standard input
    /// is the new file. Exit 2 means MediaMTX did not come up and the backup was restored.
    /// </summary>
    private const string ApplyScript = """
        conf="$1"; restart="$2"
        export LC_ALL=C
        backup="$conf.bak-$(date +%Y%m%d-%H%M%S)"
        cp -p "$conf" "$backup" || exit 1
        echo "Backup: $backup"
        if ! cat > "$conf"; then cp -p "$backup" "$conf"; exit 1; fi
        rollback() { cp -p "$backup" "$conf"; echo "The previous configuration was restored." >&2; }
        case "$restart" in
          systemd:*)
            u="${restart#systemd:}"
            systemctl restart "$u"
            sleep 3
            if ! systemctl is-active --quiet "$u"; then
              echo "MediaMTX did not start with the new configuration:" >&2
              journalctl -u "$u" -n 15 --no-pager 2>/dev/null >&2
              rollback; systemctl restart "$u"; exit 2
            fi
            echo "Restarted $u." ;;
          docker:*)
            c="${restart#docker:}"
            docker restart "$c" >/dev/null
            sleep 3
            if [ "$(docker inspect -f '{{.State.Running}}' "$c" 2>/dev/null)" != "true" ]; then
              echo "MediaMTX did not start with the new configuration:" >&2
              docker logs --tail 15 "$c" >&2 2>&1
              rollback; docker restart "$c" >/dev/null; exit 2
            fi
            echo "Restarted container $c." ;;
        esac
        if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active'; then echo "ufw=active"; fi
        exit 0
        """;
}
