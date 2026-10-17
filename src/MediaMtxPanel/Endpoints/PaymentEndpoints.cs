using MediaMtxPanel.Data;
using MediaMtxPanel.Options;
using MediaMtxPanel.Services.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Endpoints;

/// <summary>Public endpoints of the paywall: start a purchase, provider webhooks, simulated payments.</summary>
public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        // Form post from the watch page. Antiforgery is validated because the form carries a token.
        app.MapPost("/watch/{slug}/buy", async (
            string slug,
            [FromForm] int offerId,
            [FromForm] PaymentProvider provider,
            [FromForm] string? email,
            HttpContext http,
            PaywallService paywall,
            IOptions<PanelOptions> options) =>
        {
            try
            {
                var url = await paywall.StartPurchaseAsync(slug, offerId, provider, email, PublicBaseUrl(http, options.Value), http.RequestAborted);
                return Results.Redirect(url);
            }
            catch (PaymentException ex)
            {
                return Results.Redirect($"{http.Request.PathBase}/watch/{Uri.EscapeDataString(slug)}?error={Uri.EscapeDataString(ex.Message)}");
            }
        }).AllowAnonymous();

        // Mollie calls this with "id=tr_..." whenever a payment changes. We never trust the body:
        // the status is fetched from Mollie with the owner's key.
        app.MapPost("/api/payments/mollie/webhook", async ([FromForm] string? id, PaywallService paywall, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(id))
                return Results.BadRequest();
            var orderId = await paywall.OrderIdForProviderPaymentAsync(PaymentProvider.Mollie, id, ct);
            if (orderId is not null)
                await paywall.RefreshOrderAsync(orderId.Value, ct);
            // Unknown ids also get 200, so Mollie stops retrying and ids cannot be probed.
            return Results.Ok();
        }).AllowAnonymous().DisableAntiforgery();

        app.MapPost("/watch/{slug}/order/{id:guid}/simulate", async (string slug, Guid id, HttpContext http, PaywallService paywall, CancellationToken ct) =>
        {
            var order = await paywall.CompleteTestPaymentAsync(id, ct);
            return order is null ? Results.NotFound() : Results.Redirect($"{http.Request.PathBase}/watch/{Uri.EscapeDataString(slug)}/order/{id}");
        }).AllowAnonymous();

        return app;
    }

    public static string PublicBaseUrl(HttpContext http, PanelOptions options) =>
        !string.IsNullOrWhiteSpace(options.PublicUrl)
            ? options.PublicUrl.TrimEnd('/')
            : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
}
