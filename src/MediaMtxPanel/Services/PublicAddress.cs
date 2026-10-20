using MediaMtxPanel.Options;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Services;

/// <summary>
/// The address at which visitors reach the panel: Settings → Web server → Reverse proxy, otherwise
/// Panel:PublicUrl from the configuration. Null when neither is set (then the current request decides).
/// </summary>
public sealed class PublicAddress(PanelSettingsStore settings, IOptions<PanelOptions> options)
{
    public bool BehindReverseProxy => settings.Current.ReverseProxy?.Enabled == true;

    public string? Configured
    {
        get
        {
            var fromSettings = settings.Current.ReverseProxy?.PublicUrl;
            var url = !string.IsNullOrWhiteSpace(fromSettings) ? fromSettings : options.Value.PublicUrl;
            return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
        }
    }

    /// <summary><see cref="Configured"/>, or the scheme, host and path base of <paramref name="http"/>.</summary>
    public string For(HttpContext http) =>
        Configured ?? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";

    /// <summary><see cref="Configured"/>, or the base address the browser uses (NavigationManager.BaseUri).</summary>
    public string For(string baseUri) => Configured ?? baseUri.TrimEnd('/');

    /// <summary>The trusted proxies from Settings, otherwise Panel:TrustedProxies.</summary>
    public static string? TrustedProxies(PanelSettings settings, IConfiguration config)
    {
        var fromSettings = settings.ReverseProxy?.TrustedProxies;
        return !string.IsNullOrWhiteSpace(fromSettings) ? fromSettings : config["Panel:TrustedProxies"];
    }

    /// <summary>Null when <paramref name="url"/> is a usable public address, otherwise what is wrong with it.</summary>
    public static string? Validate(string? url) =>
        string.IsNullOrWhiteSpace(url) ? "Enter the public address, for example https://stream.example.com."
        : !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http") || u.Query.Length > 0
            ? "Use a URL such as https://stream.example.com."
            : null;
}
