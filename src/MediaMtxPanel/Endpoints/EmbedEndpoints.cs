using System.Net;
using System.Text.Json;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services;
using MediaMtxPanel.Services.Localization;
using MediaMtxPanel.Services.Payments;
using Microsoft.Net.Http.Headers;

namespace MediaMtxPanel.Endpoints;

/// <summary>
/// The embeddable player: /embed/{slug} is a small, self-contained page meant for an iframe on another
/// website. It honours the paywall: without a valid ticket it shows a ticket screen instead of the video.
/// </summary>
public static class EmbedEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Texts embed.js shows (translated per request); the player.js status texts are included.</summary>
    private static readonly string[] Texts =
    [
        "Live", "Play", "Pause", "Mute", "Unmute", "Volume", "Full screen", "Exit full screen", "Picture-in-picture",
        "Tap to unmute", "The broadcast will start soon.", "The broadcast hasn't started yet. The player starts automatically once there is video.",
        "This stream requires a ticket", "Buy a ticket", "Sign in", "I have a code", "Enter your ticket code", "Watch",
        "Tickets from {0}", "This code is not valid for this stream.", "This code has expired.", "Your ticket has expired or is invalid.",
        "This stream is private.", "This stream is not available.", "Stream not found", "Preview",
        "Connecting via WebRTC…", "Player could not start:", "No picture yet: the stream is not live yet", "No picture yet:",
        "Live (WebRTC)", "Connecting via HLS…", "This browser does not support HLS.", "Live (HLS)", "No picture yet (HLS):",
        "Something went wrong. Please try again.", "Loading player…",
    ];

    public static IEndpointRouteBuilder MapEmbedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/embed/{slug}", EmbedPageAsync).AllowAnonymous();

        app.MapGet("/embed/{slug}/{kind:regex(^(logo|poster)$)}", async (string slug, string kind, PaywallService paywall, EmbedService embeds, HttpContext http) =>
        {
            var stream = await paywall.StreamBySlugAsync(slug, http.RequestAborted);
            if (stream is null)
                return Results.NotFound();
            var embed = await embeds.GetAsync(stream.Id, http.RequestAborted);
            var (data, type) = kind == "logo" ? (embed.LogoData, embed.LogoContentType) : (embed.PosterData, embed.PosterContentType);
            if (data is null || type is null)
                return Results.NotFound();
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(data, type);
        }).AllowAnonymous();

        // Ticket code typed into the embedded player. No antiforgery: there is no cookie to protect, and
        // a third-party iframe would not get the antiforgery cookie anyway.
        app.MapPost("/api/embed/{slug}/redeem", async (string slug, RedeemRequest body, PaywallService paywall, HttpContext http) =>
        {
            var stream = await paywall.StreamBySlugAsync(slug, http.RequestAborted);
            if (stream is not { Enabled: true, PaywallEnabled: true })
                return Results.NotFound(new { ok = false, error = "notfound" });
            var (status, ticket) = await paywall.RedeemCodeAsync(stream.Id, body.Code, await ViewerAccounts.EmailAsync(http), http.RequestAborted);
            return status == RedeemStatus.Ok
                ? Results.Ok(new { ok = true, token = ticket!.AccessToken })
                : Results.Ok(new { ok = false, error = status == RedeemStatus.Expired ? "expired" : "invalid" });
        }).AllowAnonymous().DisableAntiforgery();

        return app;
    }

    public record RedeemRequest(string? Code);

    private static async Task<IResult> EmbedPageAsync(
        string slug, HttpContext http, PaywallService paywall, EmbedService embeds, StreamEndpointBuilder endpoints,
        MediaMtxClientFactory clients, TextLocalizer l, PanelSettingsStore settings)
    {
        var ct = http.RequestAborted;
        var lang = Language(http);
        string T(string text) => l.Translate(text, lang);

        var stream = await paywall.StreamBySlugAsync(slug, ct);
        if (stream is null)
            return Page(http, lang, T("Stream not found"), null, [], settings.Current.AppName);

        var embed = await embeds.GetAsync(stream.Id, ct);
        var look = embed.Look;
        var previewParam = http.Request.Query["preview"].ToString();
        var preview = previewParam.Length > 0 && http.User.Identity?.IsAuthenticated == true && StreamService.CanManage(http.User, stream);
        // The designer previews settings that are not saved yet.
        if (preview && PreviewLook(http.Request.Query["look"]) is { } unsaved)
            look = unsaved;
        var token = http.Request.Query["t"].ToString();

        string access;
        PlayerOptions? player = null;
        var ticketInvalid = false;
        DateTime? ticketExpires = null;
        string? priceFrom = null;
        if (!stream.Enabled)
            access = "disabled";
        else if (preview && previewParam != "gate")
        {
            access = "ok";
            player = stream.PublicRead ? endpoints.Player(stream, "", "", look.Protocol) : endpoints.Player(stream, stream.ReadUser, stream.ReadPassword, look.Protocol);
        }
        else if (stream.PaywallEnabled)
        {
            var ticket = await paywall.ValidTicketAsync(stream.Id, token, ct);
            if (ticket is not null)
            {
                access = "ok";
                ticketExpires = ticket.AccessExpiresAt;
                player = endpoints.Player(stream, MediaMtxAuthEndpoint.TicketUser, ticket.AccessToken, look.Protocol);
            }
            else
            {
                access = "ticket";
                ticketInvalid = !string.IsNullOrEmpty(token);
                var offers = await paywall.OffersAsync(stream.Id, activeOnly: true, ct);
                if (offers.Count > 0 && (await paywall.ProvidersForOwnerAsync(stream.OwnerId, ct)).Count > 0)
                {
                    var cheapest = offers.MinBy(o => o.PriceCents)!;
                    priceFrom = Format.Money(cheapest.PriceCents, cheapest.Currency);
                }
            }
        }
        else if (stream.PublicRead)
        {
            access = "ok";
            player = endpoints.Player(stream, "", "", look.Protocol);
        }
        else
        {
            access = "private";
        }

        var live = false;
        if (access == "ok")
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                live = (await clients.Create(stream.Server!).GetPathAsync(stream.Name, timeout.Token))?.IsLive == true;
            }
            catch (Exception ex) when (ex is HttpRequestException or MediaMtxException or TaskCanceledException or OperationCanceledException) { }
        }

        var version = embed.UpdatedAt.Ticks;
        var config = new
        {
            slug = stream.WatchSlug,
            title = stream.DisplayName,
            access,
            live,
            preview,
            ticketInvalid,
            ticketExpires,
            priceFrom,
            player,
            look,
            logoUrl = embed.LogoData is null ? null : $"embed/{stream.WatchSlug}/logo?v={version}",
            posterUrl = embed.PosterData is null ? null : $"embed/{stream.WatchSlug}/poster?v={version}",
            watchUrl = $"watch/{stream.WatchSlug}",
            ticketsUrl = $"tickets?embed={stream.WatchSlug}",
            redeemUrl = $"api/embed/{stream.WatchSlug}/redeem",
            texts = Texts.ToDictionary(t => t, T),
        };

        var sites = StreamEmbed.ParseSites(embed.AllowedSites);
        return Page(http, lang, stream.DisplayName, JsonSerializer.Serialize(config, Json), sites, settings.Current.AppName);
    }

    private static EmbedLook? PreviewLook(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return null;
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Microsoft.AspNetCore.WebUtilities.Base64UrlTextEncoder.Decode(encoded));
            return JsonSerializer.Deserialize<EmbedLook>(json, Json)?.Normalized();
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    private static IResult Page(HttpContext http, string lang, string title, string? configJson, IReadOnlyList<string> sites, string appName)
    {
        var headers = http.Response.Headers;
        // Which websites may show the player in a frame: all of them, unless the owner limited it.
        headers.ContentSecurityPolicy = "frame-ancestors " + (sites.Count == 0 ? "*" : "'self' " + string.Join(' ', sites));
        headers.XContentTypeOptions = "nosniff";
        headers[HeaderNames.CacheControl] = "no-store";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        var v = WebUtility.UrlEncode(BuildInfo.Version);
        var basePath = $"{http.Request.PathBase}/";
        var body = configJson is null
            ? $"""<div class="mx-notfound">{WebUtility.HtmlEncode(title)}</div>"""
            : $"""<div id="mx-embed" data-config="{WebUtility.HtmlEncode(configJson)}"></div>""";
        var html = $"""
            <!DOCTYPE html>
            <html lang="{lang}">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <meta name="robots" content="noindex" />
            <base href="{WebUtility.HtmlEncode(basePath)}" />
            <title>{WebUtility.HtmlEncode(title)} · {WebUtility.HtmlEncode(appName)}</title>
            <link rel="stylesheet" href="embed/embed.css?v={v}" />
            <script src="js/player.js?v={v}"></script>
            <script src="embed/embed.js?v={v}" defer></script>
            </head>
            <body>
            {body}
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    /// <summary>?lang=, else the language chosen in the panel, else the browser language, else the panel default.</summary>
    private static string Language(HttpContext http)
    {
        if (TextLocalizer.Normalize(http.Request.Query["lang"]) is { } fromQuery)
            return fromQuery;
        if (http.Request.Cookies.ContainsKey(Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.DefaultCookieName))
            return TextLocalizer.Current;
        var accept = http.Request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(h => h.Quality ?? 1)
            .Select(h => TextLocalizer.Normalize(h.Value.Value?.Split('-')[0]))
            .FirstOrDefault(c => c is not null);
        return accept ?? TextLocalizer.Current;
    }
}
