using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using MediaMtxPanel.Data;
using MediaMtxPanel.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Services;

/// <summary>
/// "Authorization: Bearer &lt;Panel:ServiceToken&gt;" on /api/v1: the caller (the Build My TV Station panel)
/// acts as an administrator. No cookie is involved, so antiforgery does not apply. Without a configured
/// token the scheme never authenticates anyone and Bearer headers are ignored.
/// </summary>
public sealed class ServiceTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<PanelOptions> panelOptions) : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "ServiceToken";
    public const string ApiPrefix = "/api/v1";
    public const string UserName = "service-token";

    /// <summary>Shorter tokens are refused, so a typo cannot turn on a guessable token.</summary>
    public const int MinimumLength = 32;

    public static bool IsConfigured(PanelOptions options) => (options.ServiceToken ?? "").Length >= MinimumLength;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = panelOptions.Value.ServiceToken ?? "";
        if (token.Length < MinimumLength || !Request.Path.StartsWithSegments(ApiPrefix))
            return Task.FromResult(AuthenticateResult.NoResult());

        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!TokenEquals(header["Bearer ".Length..].Trim(), token))
        {
            Logger.LogWarning("Wrong service token on {Path} from {Remote}", Request.Path, Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid service token."));
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, UserName),
            new Claim(ClaimTypes.Role, Roles.Admin),
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    /// <summary>Constant time, also for tokens of a different length (both sides are hashed first).</summary>
    public static bool TokenEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
