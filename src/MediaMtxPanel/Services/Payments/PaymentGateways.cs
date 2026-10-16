using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaMtxPanel.Data;

namespace MediaMtxPanel.Services.Payments;

public record CheckoutRequest(
    TicketOrder Order,
    string Description,
    string ReturnUrl,
    string CancelUrl,
    string? WebhookUrl);

public record CheckoutStart(string ProviderPaymentId, string CheckoutUrl);

public class PaymentException(string message) : Exception(message);

/// <summary>One payment provider. Credentials come from the stream owner's <see cref="PaymentAccount"/>.</summary>
public interface IPaymentGateway
{
    PaymentProvider Provider { get; }

    /// <summary>Creates the payment at the provider and returns where to send the buyer.</summary>
    Task<CheckoutStart> StartAsync(CheckoutRequest request, PaymentCredentials credentials, CancellationToken ct);

    /// <summary>Asks the provider for the current state (and, for PayPal, captures an approved order).</summary>
    Task<OrderStatus> CheckAsync(TicketOrder order, PaymentCredentials credentials, CancellationToken ct);
}

/// <summary>Decrypted credentials of a payment account.</summary>
public record PaymentCredentials(string? MollieApiKey, string? PayPalClientId, string? PayPalSecret, bool PayPalSandbox);

internal static class Amount
{
    public static string Value(int cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>Mollie (iDEAL). https://docs.mollie.com/reference/create-payment</summary>
public class MollieGateway(IHttpClientFactory httpClientFactory) : IPaymentGateway
{
    public PaymentProvider Provider => PaymentProvider.Mollie;

    private HttpClient Client(PaymentCredentials c)
    {
        if (string.IsNullOrEmpty(c.MollieApiKey))
            throw new PaymentException("Er is geen Mollie API-sleutel ingesteld.");
        var http = httpClientFactory.CreateClient("payments");
        http.BaseAddress = new Uri("https://api.mollie.com/v2/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", c.MollieApiKey);
        return http;
    }

    public async Task<CheckoutStart> StartAsync(CheckoutRequest r, PaymentCredentials c, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["amount"] = new JsonObject { ["currency"] = r.Order.Currency, ["value"] = Amount.Value(r.Order.AmountCents) },
            ["description"] = r.Description,
            ["redirectUrl"] = r.ReturnUrl,
            ["cancelUrl"] = r.CancelUrl,
            ["method"] = "ideal",
            ["metadata"] = new JsonObject { ["orderId"] = r.Order.Id.ToString() },
        };
        // Mollie refuses webhook URLs it cannot reach (localhost, private networks); the return page checks the status too.
        if (r.WebhookUrl is not null)
            body["webhookUrl"] = r.WebhookUrl;

        using var res = await Client(c).PostAsJsonAsync("payments", body, ct);
        var json = await ReadAsync(res, ct);
        var id = json["id"]?.GetValue<string>();
        var checkout = json["_links"]?["checkout"]?["href"]?.GetValue<string>();
        if (id is null || checkout is null)
            throw new PaymentException("Mollie gaf geen betaallink terug.");
        return new CheckoutStart(id, checkout);
    }

    public async Task<OrderStatus> CheckAsync(TicketOrder order, PaymentCredentials c, CancellationToken ct)
    {
        using var res = await Client(c).GetAsync($"payments/{Uri.EscapeDataString(order.ProviderPaymentId!)}", ct);
        var json = await ReadAsync(res, ct);
        return json["status"]?.GetValue<string>() switch
        {
            "paid" => OrderStatus.Paid,
            "canceled" => OrderStatus.Canceled,
            "expired" => OrderStatus.Expired,
            "failed" => OrderStatus.Failed,
            _ => OrderStatus.Open, // open, pending, authorized
        };
    }

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch (JsonException) { }
        if (!res.IsSuccessStatusCode || json is null)
            throw new PaymentException($"Mollie: {json?["detail"]?.GetValue<string>() ?? res.ReasonPhrase}");
        return json;
    }
}

/// <summary>PayPal Orders v2. https://developer.paypal.com/docs/api/orders/v2/</summary>
public class PayPalGateway(IHttpClientFactory httpClientFactory) : IPaymentGateway
{
    public PaymentProvider Provider => PaymentProvider.PayPal;

    private async Task<HttpClient> ClientAsync(PaymentCredentials c, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(c.PayPalClientId) || string.IsNullOrEmpty(c.PayPalSecret))
            throw new PaymentException("Er zijn geen PayPal-gegevens ingesteld.");
        var http = httpClientFactory.CreateClient("payments");
        http.BaseAddress = new Uri(c.PayPalSandbox ? "https://api-m.sandbox.paypal.com/" : "https://api-m.paypal.com/");

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "v1/oauth2/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.PayPalClientId}:{c.PayPalSecret}")));
        using var tokenResponse = await http.SendAsync(tokenRequest, ct);
        var token = (await ReadAsync(tokenResponse, ct))["access_token"]?.GetValue<string>()
            ?? throw new PaymentException("PayPal gaf geen toegangstoken terug.");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public async Task<CheckoutStart> StartAsync(CheckoutRequest r, PaymentCredentials c, CancellationToken ct)
    {
        var http = await ClientAsync(c, ct);
        var body = new JsonObject
        {
            ["intent"] = "CAPTURE",
            ["purchase_units"] = new JsonArray(new JsonObject
            {
                ["reference_id"] = r.Order.Id.ToString(),
                ["custom_id"] = r.Order.Id.ToString(),
                ["description"] = r.Description.Length > 127 ? r.Description[..127] : r.Description,
                ["amount"] = new JsonObject { ["currency_code"] = r.Order.Currency, ["value"] = Amount.Value(r.Order.AmountCents) },
            }),
            ["payment_source"] = new JsonObject
            {
                ["paypal"] = new JsonObject
                {
                    ["experience_context"] = new JsonObject
                    {
                        ["return_url"] = r.ReturnUrl,
                        ["cancel_url"] = r.CancelUrl,
                        ["user_action"] = "PAY_NOW",
                        ["shipping_preference"] = "NO_SHIPPING",
                    },
                },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "v2/checkout/orders") { Content = JsonContent.Create(body) };
        request.Headers.Add("PayPal-Request-Id", r.Order.Id.ToString());
        using var res = await http.SendAsync(request, ct);
        var json = await ReadAsync(res, ct);
        var id = json["id"]?.GetValue<string>();
        var link = json["links"]?.AsArray()
            .FirstOrDefault(l => l?["rel"]?.GetValue<string>() is "payer-action" or "approve")?["href"]?.GetValue<string>();
        if (id is null || link is null)
            throw new PaymentException("PayPal gaf geen betaallink terug.");
        return new CheckoutStart(id, link);
    }

    public async Task<OrderStatus> CheckAsync(TicketOrder order, PaymentCredentials c, CancellationToken ct)
    {
        var http = await ClientAsync(c, ct);
        var id = Uri.EscapeDataString(order.ProviderPaymentId!);

        using var get = await http.GetAsync($"v2/checkout/orders/{id}", ct);
        var status = (await ReadAsync(get, ct))["status"]?.GetValue<string>();
        if (status == "APPROVED")
        {
            // The buyer approved; money only moves once we capture.
            using var capture = new HttpRequestMessage(HttpMethod.Post, $"v2/checkout/orders/{id}/capture")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            capture.Headers.Add("PayPal-Request-Id", $"capture-{order.Id}");
            using var res = await http.SendAsync(capture, ct);
            status = (await ReadAsync(res, ct))["status"]?.GetValue<string>();
        }

        return status switch
        {
            "COMPLETED" => OrderStatus.Paid,
            "VOIDED" => OrderStatus.Canceled,
            _ => OrderStatus.Open, // CREATED, SAVED, APPROVED, PAYER_ACTION_REQUIRED
        };
    }

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch (JsonException) { }
        if (!res.IsSuccessStatusCode || json is null)
        {
            var detail = json?["details"]?[0]?["description"]?.GetValue<string>()
                         ?? json?["message"]?.GetValue<string>()
                         ?? json?["error_description"]?.GetValue<string>()
                         ?? res.ReasonPhrase;
            throw new PaymentException($"PayPal: {detail}");
        }
        return json;
    }
}

/// <summary>
/// Simulated provider for trying the paywall without a payment account. Only registered when
/// Panel:EnableTestPayments is true. The "checkout" is the panel's own order page with a pay button.
/// </summary>
public class TestGateway : IPaymentGateway
{
    public PaymentProvider Provider => PaymentProvider.Test;

    public Task<CheckoutStart> StartAsync(CheckoutRequest r, PaymentCredentials c, CancellationToken ct) =>
        Task.FromResult(new CheckoutStart($"test_{r.Order.Id:N}", r.ReturnUrl + (r.ReturnUrl.Contains('?') ? "&" : "?") + "test=1"));

    public Task<OrderStatus> CheckAsync(TicketOrder order, PaymentCredentials c, CancellationToken ct) =>
        Task.FromResult(order.Status);
}
