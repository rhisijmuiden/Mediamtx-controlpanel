using System.Security.Claims;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Tests;

public sealed class EntraSsoTests : IDisposable
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "66666666-7777-8888-9999-000000000000";

    private readonly string settingsPath = Path.Combine(Directory.CreateTempSubdirectory("entra-tests-").FullName, "panel-settings.json");
    private readonly PanelFactory factory;

    public EntraSsoTests()
    {
        factory = new PanelFactory(new Dictionary<string, string?> { ["Panel:SettingsPath"] = settingsPath });
    }

    private void Enable(bool createAccounts = false, string defaultRole = Roles.User)
    {
        var store = factory.Services.GetRequiredService<PanelSettingsStore>();
        var sso = factory.Services.GetRequiredService<EntraSso>();
        store.Save(store.Current with
        {
            Entra = new EntraSettings
            {
                Enabled = true, TenantId = Tenant, ClientId = Client, ClientSecretProtected = sso.Protect("secret"),
                CreateAccounts = createAccounts, DefaultRole = defaultRole,
            },
        });
    }

    private async Task<AuthenticationScheme?> Scheme() =>
        await factory.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(EntraSso.Scheme);

    [Fact]
    public async Task The_Microsoft_button_and_scheme_follow_the_settings()
    {
        var client = factory.Client();
        Assert.DoesNotContain("Sign in with Microsoft", await client.GetStringAsync("/Account/Login"));
        Assert.Null(await Scheme());

        Enable();
        Assert.Contains("Sign in with Microsoft", await client.GetStringAsync("/Account/Login"));
        Assert.NotNull(await Scheme());
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(EntraSso.Scheme);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/v2.0", options.Authority);
        Assert.Equal(Client, options.ClientId);
        Assert.Equal("secret", options.ClientSecret);
        Assert.Equal("/signin-entra", options.CallbackPath.Value);

        var store = factory.Services.GetRequiredService<PanelSettingsStore>();
        store.Save(store.Current with { Entra = store.Current.Entra! with { Enabled = false } });
        Assert.Null(await Scheme());
        Assert.DoesNotContain("Sign in with Microsoft", await client.GetStringAsync("/Account/Login"));
    }

    [Theory]
    [InlineData(Tenant, true)]
    [InlineData("contoso.onmicrosoft.com", true)]
    [InlineData("common", false)]
    [InlineData("organizations", false)]
    [InlineData("", false)]
    [InlineData("not a tenant", false)]
    public void Only_a_single_tenant_is_accepted(string tenant, bool valid) =>
        Assert.Equal(valid, EntraSso.IsValidTenant(tenant));

    private static ExternalLoginInfo Login(string oid, string? email, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, oid), new("name", "Test Person") };
        if (email is not null)
            claims.Add(new Claim("preferred_username", email));
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        return new ExternalLoginInfo(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), EntraSso.Scheme, oid, EntraSso.DisplayName);
    }

    private async Task<(EntraMatch, ApplicationUser?, IList<string>)> Prepare(ExternalLoginInfo info)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var (match, email) = await factory.Services.GetRequiredService<EntraSso>().PrepareAsync(users, info);
        var user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        return (match, user, user is null ? [] : await users.GetRolesAsync(user));
    }

    [Fact]
    public async Task Unknown_people_cannot_sign_in_unless_accounts_are_created()
    {
        Enable();
        var (match, user, _) = await Prepare(Login("oid-1", "new@example.com"));
        Assert.Equal(EntraMatch.NoAccount, match);
        Assert.Null(user);

        Enable(createAccounts: true, defaultRole: Roles.Customer);
        (match, user, var roles) = await Prepare(Login("oid-1", "new@example.com"));
        Assert.Equal(EntraMatch.Ready, match);
        Assert.Equal("new@example.com", user!.Email);
        Assert.Equal("Test Person", user.DisplayName);
        Assert.Equal([Roles.Customer], roles);
    }

    [Fact]
    public async Task An_existing_account_is_linked_by_email_and_takes_the_app_role()
    {
        Enable();
        var admin = factory.Services.GetRequiredService<UserAdminService>();
        Assert.True((await admin.CreateAsync("staff@example.com", null, "Some-Password-1", Roles.User)).Succeeded);

        var (match, user, roles) = await Prepare(Login("oid-2", "Staff@Example.com", "Admin"));
        Assert.Equal(EntraMatch.Ready, match);
        Assert.Equal("staff@example.com", user!.Email);
        Assert.Equal([Roles.Admin], roles);

        // Next sign-in finds the account by its linked login, even without an e-mail claim.
        (match, _, roles) = await Prepare(Login("oid-2", null));
        Assert.Equal(EntraMatch.Ready, match);
        Assert.Equal([Roles.Admin], roles);
    }

    [Fact]
    public async Task A_token_without_email_is_refused() =>
        Assert.Equal(EntraMatch.NoEmail, (await Prepare(Login("oid-3", null))).Item1);

    public void Dispose()
    {
        factory.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(settingsPath)!, recursive: true); } catch (IOException) { }
    }
}
