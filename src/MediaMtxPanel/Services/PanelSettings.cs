using System.Text.Json;
using System.Text.RegularExpressions;
using MediaMtxPanel.Services.Localization;
using Microsoft.Data.Sqlite;

namespace MediaMtxPanel.Services;

/// <summary>Settings an administrator changes in the panel under Settings.</summary>
public record PanelSettings
{
    public const string DefaultAppName = "MediaMTX Panel";

    /// <summary>Name shown in the menu, the browser tab and on the login page.</summary>
    public string AppName { get; init; } = DefaultAppName;

    /// <summary>Language for visitors who have not picked one themselves.</summary>
    public string DefaultLanguage { get; init; } = TextLocalizer.DefaultLanguage;

    /// <summary>Port of the web interface; null keeps the port from ASPNETCORE_URLS.</summary>
    public int? WebPort { get; init; }

    /// <summary>Check GitHub for a newer version every 24 hours.</summary>
    public bool AutoUpdateCheck { get; init; } = true;
}

/// <summary>
/// Keeps <see cref="PanelSettings"/> in <c>panel-settings.json</c> next to the database, so the
/// web port can be read before the web server starts (the database is not available yet then).
/// </summary>
public sealed class PanelSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock gate = new();

    public PanelSettingsStore(string path)
    {
        FilePath = path;
        Current = Load(path);
    }

    public string FilePath { get; }
    public PanelSettings Current { get; private set; }

    /// <summary>Raised after the settings were saved; the layout uses it to show a new name right away.</summary>
    public event Action? Changed;

    public void Save(PanelSettings settings)
    {
        settings = settings with
        {
            AppName = string.IsNullOrWhiteSpace(settings.AppName) ? PanelSettings.DefaultAppName : settings.AppName.Trim(),
            DefaultLanguage = TextLocalizer.Normalize(settings.DefaultLanguage) ?? TextLocalizer.DefaultLanguage,
        };
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, FilePath, overwrite: true);
            Current = settings;
        }
        Changed?.Invoke();
    }

    public static PanelSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>
    /// <c>Panel:SettingsPath</c> when set, otherwise <c>panel-settings.json</c> in the folder of the SQLite database
    /// (the data volume in Docker, /var/lib/mediamtx-panel in the Proxmox container).
    /// </summary>
    public static string ResolvePath(IConfiguration config, string contentRoot)
    {
        var configured = config["Panel:SettingsPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured, contentRoot);
        var dataSource = new SqliteConnectionStringBuilder(config.GetConnectionString("DefaultConnection") ?? "").DataSource;
        var dir = string.IsNullOrEmpty(dataSource) ? contentRoot : Path.GetDirectoryName(Path.GetFullPath(dataSource, contentRoot))!;
        return Path.Combine(dir, "panel-settings.json");
    }
}

/// <summary>
/// The addresses the web server listens on. When a port was chosen under Settings it replaces the
/// port of ASPNETCORE_URLS. The original port stays open on 127.0.0.1, because a MediaMTX on the same
/// machine calls the authentication hook there (authHTTPAddress in mediamtx.yml).
/// </summary>
public sealed partial class WebPortInfo
{
    public int ConfiguredPort { get; private init; }
    public int ActivePort { get; private init; }
    public IReadOnlyList<string> Urls { get; private init; } = [];

    /// <summary>In a container the port is published by Docker, so changing it inside would only break things.</summary>
    public bool InContainer { get; private init; }

    public static WebPortInfo Apply(WebApplicationBuilder builder, PanelSettings settings)
    {
        var configured = builder.Configuration["urls"];
        var urls = string.IsNullOrWhiteSpace(configured)
            ? ["http://localhost:5000"]
            : configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var configuredPort = urls.Select(PortOf).FirstOrDefault(p => p > 0, 80);
        var inContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

        var port = settings.WebPort is >= 1 and <= 65535 && !inContainer ? settings.WebPort.Value : configuredPort;
        if (port != configuredPort)
        {
            urls = urls.Select(u => PortPattern().Replace(u, $":{port}")).ToList();
            urls.Add($"http://127.0.0.1:{configuredPort}");
            builder.WebHost.UseUrls([.. urls]);
        }
        return new WebPortInfo { ConfiguredPort = configuredPort, ActivePort = port, Urls = urls, InContainer = inContainer };
    }

    /// <summary>Replaces the configured port with the active one in an address such as Panel:HookBaseUrl.</summary>
    public string Rewrite(string url) =>
        ActivePort == ConfiguredPort || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Port != ConfiguredPort
            ? url
            : new UriBuilder(u) { Port = ActivePort }.Uri.ToString().TrimEnd('/');

    private static int PortOf(string url)
    {
        var m = PortPattern().Match(url);
        return m.Success ? int.Parse(m.Groups[1].Value) : url.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
    }

    [GeneratedRegex(@":(\d+)(?=/?$)")]
    private static partial Regex PortPattern();
}
