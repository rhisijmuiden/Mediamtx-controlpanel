using MediaMtxPanel.Services;
using MediaMtxPanel.Services.Payments;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace MediaMtxPanel.Endpoints;

/// <summary>Form posts of the viewer pages: "My tickets" sign-in and ticket codes on the watch page.</summary>
public static class ViewerEndpoints
{
    public static IEndpointRouteBuilder MapViewerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/tickets/send-code", async ([FromForm] string? email, [FromForm] string? embed, HttpContext http, ViewerAccounts viewers) =>
        {
            var normalized = PaywallService.NormalizeEmail(email);
            if (normalized is null)
                return Back(http, "tickets", ("error", "email"), ("embed", embed));
            var sent = await viewers.SendCodeAsync(normalized, http.RequestAborted);
            return Back(http, "tickets", ("step", "code"), ("email", normalized), ("embed", embed), ("error", sent ? null : "limit"));
        }).AllowAnonymous();

        app.MapPost("/tickets/verify", async ([FromForm] string? email, [FromForm] string? code, [FromForm] string? embed, HttpContext http, ViewerAccounts viewers) =>
            await viewers.VerifyAndSignInAsync(http, email, code, http.RequestAborted)
                ? Back(http, "tickets", ("embed", embed))
                : Back(http, "tickets", ("step", "code"), ("email", email), ("embed", embed), ("error", "code"))).AllowAnonymous();

        app.MapPost("/tickets/sign-out", async (HttpContext http) =>
        {
            await http.SignOutAsync(ViewerAccounts.Scheme);
            return Back(http, "tickets");
        }).AllowAnonymous();

        app.MapPost("/tickets/redeem", async ([FromForm] string? code, HttpContext http, PaywallService paywall) =>
        {
            var (status, ticket) = await paywall.RedeemCodeAsync(null, code, await ViewerAccounts.EmailAsync(http), http.RequestAborted);
            return status == RedeemStatus.Ok && ticket!.Stream is { Enabled: true } s
                ? Back(http, $"watch/{Uri.EscapeDataString(s.WatchSlug)}", ("t", ticket.AccessToken))
                : Back(http, "tickets", ("code", status == RedeemStatus.Expired ? "expired" : "invalid"));
        }).AllowAnonymous();

        app.MapPost("/watch/{slug}/redeem", async (string slug, [FromForm] string? code, HttpContext http, PaywallService paywall) =>
        {
            var stream = await paywall.StreamBySlugAsync(slug, http.RequestAborted);
            if (stream is not { Enabled: true, PaywallEnabled: true })
                return Back(http, $"watch/{Uri.EscapeDataString(slug)}");
            var (status, ticket) = await paywall.RedeemCodeAsync(stream.Id, code, await ViewerAccounts.EmailAsync(http), http.RequestAborted);
            return status == RedeemStatus.Ok
                ? Back(http, $"watch/{Uri.EscapeDataString(slug)}", ("t", ticket!.AccessToken))
                : Back(http, $"watch/{Uri.EscapeDataString(slug)}", ("code", status == RedeemStatus.Expired ? "expired" : "invalid"));
        }).AllowAnonymous();

        return app;
    }

    private static IResult Back(HttpContext http, string path, params (string Key, string? Value)[] query)
    {
        var q = string.Join('&', query.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));
        return Results.Redirect($"{http.Request.PathBase}/{path}{(q.Length > 0 ? "?" + q : "")}");
    }
}
