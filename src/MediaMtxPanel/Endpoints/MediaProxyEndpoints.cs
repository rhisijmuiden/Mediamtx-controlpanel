using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy.Forwarder;

namespace MediaMtxPanel.Endpoints;

/// <summary>
/// Forwards HLS and WebRTC (WHEP/WHIP signalling, reader.js, the browser pages) of a server whose viewer
/// route is "through the panel": /media/{server}/hls/... and /media/{server}/webrtc/... go to the MediaMTX
/// HLS and WebRTC ports on the host of the server's API URL. Viewers then only need the panel's own
/// (https) address, which is what a reverse proxy in front of the panel provides. WebRTC media itself
/// still flows over UDP/TCP 8189 straight to MediaMTX; only the signalling passes through here.
/// </summary>
public static class MediaProxyEndpoints
{
    private static readonly HttpMessageInvoker Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        EnableMultipleHttp2Connections = true,
        ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    });

    public static IEndpointRouteBuilder MapMediaProxy(this IEndpointRouteBuilder app)
    {
        app.Map(StreamEndpointBuilder.ProxyPrefix + "/{serverId:int}/{kind:regex(^(hls|webrtc)$)}/{**rest}", async (
            int serverId, string kind, string? rest, HttpContext http, IHttpForwarder forwarder,
            StreamEndpointBuilder endpoints, MediaProxyServers servers) =>
        {
            // MediaMTX's own 401/404 answers must reach the player as they are, not as the panel's not-found page.
            if (http.Features.Get<IStatusCodePagesFeature>() is { } pages)
                pages.Enabled = false;

            var server = await servers.FindAsync(serverId, http.RequestAborted);
            if (server is null || !server.Enabled || endpoints.RouteOf(server) != ViewerRoute.Panel
                || !Uri.TryCreate(server.ApiUrl, UriKind.Absolute, out var api))
                return Results.NotFound();

            var port = kind == "hls" ? server.HlsPort : server.WebRtcPort;
            var target = new UriBuilder("http", api.Host, port).Uri.ToString().TrimEnd('/');
            var prefix = $"{http.Request.PathBase}{StreamEndpointBuilder.ProxyPrefix}/{serverId}/{kind}";
            var error = await forwarder.SendAsync(http, target, Client, ForwarderRequestConfig.Empty, new Transformer(rest ?? "", prefix));
            if (error != ForwarderError.None && !http.Response.HasStarted)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Empty;
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .WithMetadata(new SkipStatusCodePagesAttribute());
        return app;
    }

    /// <summary>Sends the request to the path after the prefix and points redirects and WHEP session URLs back at the prefix.</summary>
    private sealed class Transformer(string rest, string prefix) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext http, HttpRequestMessage proxyRequest,
            string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(http, proxyRequest, destinationPrefix, cancellationToken);
            var path = string.Join('/', rest.Split('/').Select(Uri.EscapeDataString));
            proxyRequest.RequestUri = new Uri($"{destinationPrefix}/{path}{http.Request.QueryString}");
            proxyRequest.Headers.Host = null;
            // The viewer's address (already taken from the reverse proxy's X-Forwarded-For by UseForwardedHeaders);
            // MediaMTX uses it when the panel is listed in hlsTrustedProxies / webrtcTrustedProxies.
            proxyRequest.Headers.Remove("X-Forwarded-For");
            if (http.Connection.RemoteIpAddress is { } ip)
                proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", ip.ToString());
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext http, HttpResponseMessage? proxyResponse,
            CancellationToken cancellationToken)
        {
            var result = await base.TransformResponseAsync(http, proxyResponse, cancellationToken);
            var location = http.Response.Headers.Location.ToString();
            if (location.StartsWith('/') && !location.StartsWith("//", StringComparison.Ordinal))
                http.Response.Headers.Location = prefix + location;
            return result;
        }
    }
}

/// <summary>
/// The servers the media proxy forwards to. HLS players ask for a playlist or segment every second or so,
/// so the server is not read from the database for each request.
/// </summary>
public sealed class MediaProxyServers(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<int, (MediaServer? Server, DateTime Until)> cache = new();

    public async Task<MediaServer?> FindAsync(int id, CancellationToken ct)
    {
        if (cache.TryGetValue(id, out var cached) && cached.Until > DateTime.UtcNow)
            return cached.Server;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        cache[id] = (server, DateTime.UtcNow + CacheFor);
        return server;
    }

    /// <summary>Forget cached servers, so a changed viewer route or address applies right away.</summary>
    public void Invalidate() => cache.Clear();
}
