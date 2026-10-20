using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaMtxPanel.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaMtxPanel.Tests;

public class EmbedTests
{
    private static async Task<StreamDefinition> AddStreamAsync(PanelFactory factory, Action<StreamDefinition> configure)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var server = await db.Servers.FirstAsync();
        var stream = new StreamDefinition
        {
            ServerId = server.Id,
            Name = "s" + CredentialGenerator.Slug(6),
            DisplayName = "Test stream",
            WatchSlug = CredentialGenerator.Slug(),
            ReadUser = "viewer",
            ReadPassword = "secret",
        };
        configure(stream);
        db.Streams.Add(stream);
        await db.SaveChangesAsync();
        return stream;
    }

    private static async Task<TicketOrder> AddCodeAsync(PanelFactory factory, int streamId, DateTime? redeemBefore = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var order = new TicketOrder
        {
            StreamId = streamId,
            OfferName = "Access code",
            AccessHours = 2,
            Provider = PaymentProvider.Manual,
            Status = OrderStatus.Paid,
            AccessToken = CredentialGenerator.Password(32),
            Code = TicketCode.New(),
            RedeemBefore = redeemBefore,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static JsonElement Config(string html)
    {
        const string marker = "data-config=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = html.IndexOf('"', start);
        return JsonDocument.Parse(WebUtility.HtmlDecode(html[start..end])).RootElement;
    }

    [Fact]
    public async Task Public_stream_plays_and_may_be_framed_anywhere()
    {
        using var factory = new PanelFactory();
        var stream = await AddStreamAsync(factory, s => s.PublicRead = true);

        var response = await factory.Client().GetAsync($"/embed/{stream.WatchSlug}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("frame-ancestors *", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(response.Headers.Contains("X-Frame-Options"));
        var config = Config(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", config.GetProperty("access").GetString());
        Assert.Equal("", config.GetProperty("player").GetProperty("user").GetString());
    }

    [Fact]
    public async Task Private_stream_does_not_leak_credentials()
    {
        using var factory = new PanelFactory();
        var stream = await AddStreamAsync(factory, _ => { });

        var html = await factory.Client().GetStringAsync($"/embed/{stream.WatchSlug}");

        var config = Config(html);
        Assert.Equal("private", config.GetProperty("access").GetString());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("player").ValueKind);
        Assert.DoesNotContain("secret", html);
    }

    [Fact]
    public async Task Allowed_sites_limit_frame_ancestors()
    {
        using var factory = new PanelFactory();
        var stream = await AddStreamAsync(factory, s => s.PublicRead = true);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Embeds.Add(new StreamEmbed
            {
                StreamId = stream.Id,
                AllowedSites = string.Join('\n', StreamEmbed.ParseSites("https://www.example.com/\n*.example.org\n<script>")),
            });
            await db.SaveChangesAsync();
        }

        var response = await factory.Client().GetAsync($"/embed/{stream.WatchSlug}");

        Assert.Equal("frame-ancestors 'self' www.example.com *.example.org", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Paywalled_stream_shows_the_ticket_screen_until_a_code_is_redeemed()
    {
        using var factory = new PanelFactory();
        var stream = await AddStreamAsync(factory, s => s.PaywallEnabled = true);
        var code = await AddCodeAsync(factory, stream.Id);
        var client = factory.Client();

        var gate = Config(await client.GetStringAsync($"/embed/{stream.WatchSlug}"));
        Assert.Equal("ticket", gate.GetProperty("access").GetString());

        var redeem = await client.PostAsJsonAsync($"/api/embed/{stream.WatchSlug}/redeem", new { code = TicketCode.Display(code.Code).ToLowerInvariant() });
        var result = await redeem.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(code.AccessToken, result.GetProperty("token").GetString());

        var watching = Config(await client.GetStringAsync($"/embed/{stream.WatchSlug}?t={code.AccessToken}"));
        Assert.Equal("ok", watching.GetProperty("access").GetString());
        Assert.Equal("ticket", watching.GetProperty("player").GetProperty("user").GetString());

        // The access period started at first use.
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Orders.SingleAsync(o => o.Id == code.Id);
        Assert.InRange(stored.AccessExpiresAt!.Value, DateTime.UtcNow.AddHours(1.9), DateTime.UtcNow.AddHours(2.1));
    }

    [Fact]
    public async Task Codes_are_rejected_after_their_deadline_or_for_another_stream()
    {
        using var factory = new PanelFactory();
        var stream = await AddStreamAsync(factory, s => s.PaywallEnabled = true);
        var other = await AddStreamAsync(factory, s => s.PaywallEnabled = true);
        var late = await AddCodeAsync(factory, stream.Id, DateTime.UtcNow.AddDays(-1));
        var foreign = await AddCodeAsync(factory, other.Id);
        var client = factory.Client();

        var expired = await (await client.PostAsJsonAsync($"/api/embed/{stream.WatchSlug}/redeem", new { code = late.Code }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var invalid = await (await client.PostAsJsonAsync($"/api/embed/{stream.WatchSlug}/redeem", new { code = foreign.Code }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("expired", expired.GetProperty("error").GetString());
        Assert.Equal("invalid", invalid.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Preview_settings_need_a_panel_login()
    {
        using var factory = new PanelFactory();
        var stream = await AddStreamAsync(factory, _ => { });

        var config = Config(await factory.Client().GetStringAsync($"/embed/{stream.WatchSlug}?preview=1"));

        Assert.False(config.GetProperty("preview").GetBoolean());
        Assert.Equal("private", config.GetProperty("access").GetString());
    }

    [Fact]
    public async Task My_tickets_page_answers_anonymously()
    {
        using var factory = new PanelFactory();
        var response = await factory.Client().GetAsync("/tickets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_signs_in_with_a_mailed_code_and_sees_their_tickets()
    {
        using var factory = new PanelFactory();
        var stream = await AddStreamAsync(factory, s => s.PaywallEnabled = true);
        var ticket = await AddCodeAsync(factory, stream.Id);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var order = await db.Orders.SingleAsync(o => o.Id == ticket.Id);
            (order.BuyerEmail, order.PaidAt, order.AccessExpiresAt) = ("fan@example.com", DateTime.UtcNow, DateTime.UtcNow.AddHours(2));
            db.ViewerLoginCodes.Add(new ViewerLoginCode
            {
                Email = "fan@example.com",
                CodeHash = MediaMtxPanel.Services.ViewerAccounts.Hash("fan@example.com", "123456"),
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }

        using var scope2 = factory.Services.CreateScope();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope2.ServiceProvider };
        var viewers = scope2.ServiceProvider.GetRequiredService<MediaMtxPanel.Services.ViewerAccounts>();
        Assert.False(await viewers.VerifyAndSignInAsync(http, "Fan@Example.com", "654321"));
        Assert.True(await viewers.VerifyAndSignInAsync(http, "Fan@Example.com", "123 456"));
        Assert.Contains(MediaMtxPanel.Services.ViewerAccounts.CookieName, http.Response.Headers.SetCookie.ToString());
        // A code works only once.
        Assert.False(await viewers.VerifyAndSignInAsync(http, "fan@example.com", "123456"));

        var paywall = scope2.ServiceProvider.GetRequiredService<MediaMtxPanel.Services.Payments.PaywallService>();
        Assert.Equal(ticket.Id, (await paywall.ValidTicketForEmailAsync(stream.Id, "FAN@example.com"))?.Id);
        Assert.Single(await paywall.TicketsForEmailAsync("fan@example.com"));
    }

    [Theory]
    [InlineData("k7qm-4xpa-9trc", "K7QM4XPA9TRC")]
    [InlineData(" K7QM 4XPA 9TRC ", "K7QM4XPA9TRC")]
    [InlineData("https://tv.example.com/watch/abc?code=K7QM-4XPA-9TRC&x=1", "K7QM4XPA9TRC")]
    public void Ticket_codes_are_normalized(string input, string expected) =>
        Assert.Equal(expected, TicketCode.Normalize(input));

    [Fact]
    public void Look_is_clamped_to_safe_values()
    {
        var look = new EmbedLook { Accent = "red;}", Radius = 99, Aspect = "2:1", LogoLink = "javascript:alert(1)", Width = 10 }.Normalized();
        Assert.Equal("#6366f1", look.Accent);
        Assert.Equal(32, look.Radius);
        Assert.Equal("16:9", look.Aspect);
        Assert.Null(look.LogoLink);
        Assert.Equal(240, look.Width);
    }
}
