using System.Net;
using System.Net.Sockets;

namespace MediaMtxPanel.Services;

/// <summary>
/// Cloudflare's proxy ("orange cloud") in front of the panel: the viewer's address arrives in CF-Connecting-IP,
/// and only HTTP(S) passes, so RTSP, RTMP, SRT and WebRTC media (UDP 8189) need a host name that is not proxied.
/// </summary>
public static class Cloudflare
{
    public const string ConnectingIpHeader = "CF-Connecting-IP";

    // https://www.cloudflare.com/ips/ (the ranges change rarely).
    private static readonly IPNetwork[] Ranges =
    [
        .. new[]
        {
            "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18",
            "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17",
            "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
            "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32",
            "2a06:98c0::/29", "2c0f:f248::/32",
        }.Select(IPNetwork.Parse),
    ];

    public static bool IsCloudflare(IPAddress? address)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return Ranges.Any(r => r.Contains(address));
    }

    /// <summary>
    /// Takes the viewer's address from CF-Connecting-IP when the request reached the panel from a Cloudflare
    /// address (directly, or as the last hop a proxy such as Zoraxy put in X-Forwarded-For). Runs after
    /// UseForwardedHeaders; a request from anywhere else keeps its address, so the header cannot be spoofed
    /// by connecting to the panel directly.
    /// </summary>
    public static IApplicationBuilder UseCloudflareClientIp(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (IsCloudflare(context.Connection.RemoteIpAddress)
                && IPAddress.TryParse(context.Request.Headers[ConnectingIpHeader].ToString().Trim(), out var client))
                context.Connection.RemoteIpAddress = client;
            return next(context);
        });

    /// <summary>The Cloudflare addresses <paramref name="host"/> resolves to; empty when it does not use Cloudflare's proxy.</summary>
    public static async Task<IReadOnlyList<IPAddress>> ProxiedAddressesAsync(string? host, CancellationToken ct = default)
    {
        host = host?.Trim();
        if (string.IsNullOrEmpty(host))
            return [];
        try
        {
            var addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct);
            return addresses.Where(IsCloudflare).ToList();
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }
}
