using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaMtxPanel.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Services;

/// <summary>Microsoft Entra ID sign-in, configured under Settings → Single sign-on. Null until configured.</summary>
public record EntraSettings
{
    public bool Enabled { get; init; }

    /// <summary>Directory (tenant) ID, or a verified domain such as contoso.onmicrosoft.com.</summary>
    public string TenantId { get; init; } = "";

    /// <summary>Application (client) ID of the app registration.</summary>
    public string ClientId { get; init; } = "";

    /// <summary>Encrypted with ASP.NET Data Protection; see <see cref="EntraSso"/>.</summary>
    public string? ClientSecretProtected { get; init; }

    /// <summary>Create a panel account the first time someone from the tenant signs in.</summary>
    public bool CreateAccounts { get; init; }

    /// <summary>Role of accounts created at first sign-in, unless the token carries an app role.</summary>
    public string DefaultRole { get; init; } = Roles.User;
}

/// <summary>Result of matching a Microsoft sign-in to a panel account.</summary>
public enum EntraMatch
{
    /// <summary>The account was linked or created; sign in with the external login.</summary>
    Ready,
    /// <summary>The token carries no usable e-mail address.</summary>
    NoEmail,
    /// <summary>No account with this address and creating accounts is off.</summary>
    NoAccount,
    /// <summary>Creating or linking the account failed.</summary>
    Failed,
}

/// <summary>
/// Turns the Microsoft Entra ID login on and off without a restart: the OpenID Connect scheme is only
/// registered while it is enabled and complete, and is rebuilt whenever the settings are saved.
/// </summary>
public sealed partial class EntraSso(
    PanelSettingsStore store,
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitorCache<OpenIdConnectOptions> optionsCache,
    IDataProtectionProvider dataProtection,
    IHttpClientFactory httpFactory,
    ILogger<EntraSso> logger)
{
    public const string Scheme = "Entra";
    public const string DisplayName = "Microsoft";
    public const string CallbackPath = "/signin-entra";

    // Tenant-independent endpoints would let accounts from any organisation sign in.
    private static readonly string[] SharedTenants = ["common", "organizations", "consumers"];

    private IDataProtector Protector => dataProtection.CreateProtector("MediaMtxPanel.EntraClientSecret.v1");

    public EntraSettings? Settings => store.Current.Entra;

    /// <summary>True when the login page offers "Sign in with Microsoft".</summary>
    public bool IsActive => Settings is { Enabled: true } s && IsComplete(s);

    public static bool IsComplete(EntraSettings s) =>
        IsValidTenant(s.TenantId) && Guid.TryParse(s.ClientId, out _) && !string.IsNullOrEmpty(s.ClientSecretProtected);

    /// <summary>A GUID or a domain name; the shared multi-tenant endpoints are refused.</summary>
    public static bool IsValidTenant(string? tenant) =>
        !string.IsNullOrWhiteSpace(tenant)
        && !SharedTenants.Contains(tenant.Trim(), StringComparer.OrdinalIgnoreCase)
        && (Guid.TryParse(tenant, out _) || DomainPattern().IsMatch(tenant.Trim()));

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : Protector.Protect(secret);

    private string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return Protector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning("The stored Entra client secret cannot be decrypted (were the data protection keys replaced?)");
            return null;
        }
    }

    /// <summary>Registers the scheme for the current settings and again after every save.</summary>
    public void Start()
    {
        Apply();
        store.Changed += Apply;
    }

    private void Apply()
    {
        schemes.RemoveScheme(Scheme);
        optionsCache.TryRemove(Scheme);
        if (IsActive)
            schemes.TryAddScheme(new AuthenticationScheme(Scheme, DisplayName, typeof(OpenIdConnectHandler)));
    }

    private static string Authority(string tenant) => $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant.Trim())}/v2.0";

    /// <summary>Fills the OpenID Connect options from the saved settings.</summary>
    public void Configure(OpenIdConnectOptions o)
    {
        var s = Settings ?? new EntraSettings();
        o.Authority = Authority(s.TenantId.Length > 0 ? s.TenantId : "invalid");
        o.ClientId = s.ClientId.Trim();
        o.ClientSecret = Unprotect(s.ClientSecretProtected);
        o.CallbackPath = CallbackPath;
        o.SignInScheme = IdentityConstants.ExternalScheme;
        o.ResponseType = "code";
        o.UsePkce = true;
        o.Scope.Clear();
        foreach (var scope in new[] { "openid", "profile", "email" })
            o.Scope.Add(scope);
        o.TokenValidationParameters.NameClaimType = "name";
        o.TokenValidationParameters.RoleClaimType = "roles";
        o.MapInboundClaims = false;
        // Identity looks up the external login by NameIdentifier: use the object ID, which is stable per tenant user.
        o.Events.OnTokenValidated = context =>
        {
            if (context.Principal?.Identity is ClaimsIdentity identity && identity.FindFirst("oid") is { } oid
                && identity.FindFirst(ClaimTypes.NameIdentifier) is null)
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, oid.Value));
            return Task.CompletedTask;
        };
        o.Events.OnRemoteFailure = context =>
        {
            logger.LogWarning(context.Failure, "Microsoft sign-in failed");
            var message = Uri.EscapeDataString(context.Failure?.Message ?? "unknown error");
            context.Response.Redirect($"{context.Request.PathBase}/Account/ExternalLogin?RemoteError={message}");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    /// <summary>The e-mail address in the token: "email" when present, otherwise the sign-in name.</summary>
    public static string? EmailOf(ClaimsPrincipal principal)
    {
        foreach (var type in new[] { "email", ClaimTypes.Email, "preferred_username", "upn" })
        {
            var value = principal.FindFirstValue(type)?.Trim();
            if (!string.IsNullOrEmpty(value) && System.Net.Mail.MailAddress.TryCreate(value, out _))
                return value;
        }
        return null;
    }

    /// <summary>
    /// The panel role from the app roles in the token (app roles named Admin, User or Customer in the
    /// app registration), or null when the token carries none of them.
    /// </summary>
    public static string? RoleOf(ClaimsPrincipal principal)
    {
        var granted = principal.FindAll("roles").Concat(principal.FindAll(ClaimTypes.Role)).Select(c => c.Value).ToList();
        return Roles.All.FirstOrDefault(r => granted.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the account for a Microsoft sign-in: one already linked to it, otherwise the account with the same
    /// e-mail address (which gets linked), otherwise a new one when creating accounts is on. When the token
    /// carries an app role, the account gets that role.
    /// </summary>
    public async Task<(EntraMatch Match, string? Email)> PrepareAsync(UserManager<ApplicationUser> users, ExternalLoginInfo info)
    {
        var settings = Settings ?? new EntraSettings();
        var tokenRole = RoleOf(info.Principal);
        var user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        var email = EmailOf(info.Principal);

        if (user is null)
        {
            if (email is null)
                return (EntraMatch.NoEmail, null);
            user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                if (!settings.CreateAccounts)
                    return (EntraMatch.NoAccount, email);
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    DisplayName = info.Principal.FindFirstValue("name"),
                };
                var created = await users.CreateAsync(user);
                if (!created.Succeeded)
                    return Failed(created, email);
                var role = tokenRole ?? (Roles.All.Contains(settings.DefaultRole) ? settings.DefaultRole : Roles.User);
                var added = await users.AddToRoleAsync(user, role);
                if (!added.Succeeded)
                    return Failed(added, email);
                logger.LogInformation("Created account {Email} ({Role}) at first Microsoft sign-in", email, role);
            }
            var linked = await users.AddLoginAsync(user, info);
            if (!linked.Succeeded)
                return Failed(linked, email);
        }

        if (tokenRole is not null && !await users.IsInRoleAsync(user, tokenRole))
        {
            await users.RemoveFromRolesAsync(user, await users.GetRolesAsync(user));
            await users.AddToRoleAsync(user, tokenRole);
            logger.LogInformation("{Email} now has role {Role} from the Entra app roles", user.Email, tokenRole);
        }
        return (EntraMatch.Ready, user.Email);
    }

    private (EntraMatch, string?) Failed(IdentityResult result, string email)
    {
        logger.LogWarning("Microsoft sign-in for {Email} failed: {Errors}", email, string.Join("; ", result.Errors.Select(e => e.Description)));
        return (EntraMatch.Failed, email);
    }

    /// <summary>
    /// Checks the settings against Microsoft without a browser: the tenant must exist, and the client ID and
    /// secret must be able to get a token. Returns null when everything works, otherwise Microsoft's answer.
    /// </summary>
    public async Task<string?> TestAsync(string tenant, string clientId, string? secret, CancellationToken ct = default)
    {
        secret ??= Unprotect(Settings?.ClientSecretProtected);
        var http = httpFactory.CreateClient("entra");
        using var metadata = await http.GetAsync($"{Authority(tenant)}/.well-known/openid-configuration", ct);
        if (!metadata.IsSuccessStatusCode)
            return await ErrorOf(metadata, ct);
        if (string.IsNullOrEmpty(secret))
            return null;

        using var token = await http.PostAsync(
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant.Trim())}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId.Trim(),
                ["client_secret"] = secret,
                ["scope"] = "https://graph.microsoft.com/.default",
            }), ct);
        return token.IsSuccessStatusCode ? null : await ErrorOf(token, ct);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("error_description", out var description))
                return description.GetString()?.Split('\n')[0].Trim() ?? response.ReasonPhrase ?? "";
        }
        catch (JsonException)
        {
        }
        return $"{(int)response.StatusCode} {response.ReasonPhrase}";
    }

    [GeneratedRegex(@"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$")]
    private static partial Regex DomainPattern();
}
