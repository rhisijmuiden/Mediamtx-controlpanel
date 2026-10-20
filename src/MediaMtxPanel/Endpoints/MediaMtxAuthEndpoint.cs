using MediaMtxPanel.Data;
using MediaMtxPanel.Services;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Endpoints;

/// <summary>
/// HTTP authentication hook for MediaMTX (authMethod: http). Every managed MediaMTX server posts its
/// publish/read/api requests to /api/mediamtx/auth?secret=&lt;server hook secret&gt;. The secret tells the
/// panel which server is asking, so a stream's credentials only work on the server the stream lives on.
/// A 2xx answer allows the request, anything else denies it.
/// </summary>
public static class MediaMtxAuthEndpoint
{
    public const string TicketUser = "ticket";

    public record AuthRequest(
        string? Ip, string? User, string? Password, string? Token,
        string? Action, string? Path, string? Protocol, string? Id, string? Query);

    public static IEndpointRouteBuilder MapMediaMtxAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/mediamtx/auth", HandleAsync).AllowAnonymous().DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        AuthRequest req,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ViewerReportService reports,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MediaMtxAuth");
        var ct = http.RequestAborted;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var secret = http.Request.Query["secret"].ToString();
        var server = secret.Length == 0
            ? null
            : await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.HookSecret == secret, ct);
        if (server is null || !server.Enabled)
        {
            logger.LogWarning("Auth hook called with an unknown secret or disabled server from {Remote}",
                http.Connection.RemoteIpAddress);
            return Results.Text("unknown server", statusCode: StatusCodes.Status403Forbidden);
        }

        // Password may also arrive as a token (e.g. "Authorization: Bearer user:pass" for WHIP/WHEP).
        var (user, pass) = (req.User ?? "", req.Password ?? "");
        if (user.Length == 0 && !string.IsNullOrEmpty(req.Token) && req.Token.Contains(':'))
        {
            var parts = req.Token.Split(':', 2);
            (user, pass) = (parts[0], parts[1]);
        }

        bool allowed;
        StreamDefinition? stream = null;
        switch (req.Action)
        {
            case "api":
            case "metrics":
            case "pprof":
                allowed = server.ApiPassword.Length > 0
                    && CredentialGenerator.FixedTimeEquals(user, server.ApiUser)
                    && CredentialGenerator.FixedTimeEquals(pass, server.ApiPassword);
                break;

            case "publish":
            case "read":
            case "playback":
                var path = req.Path ?? "";
                stream = await db.Streams.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.ServerId == server.Id && s.Name == path, ct);
                allowed = stream is { Enabled: true } && IsAllowed(stream, req.Action, user, pass);
                // Paying viewers watch with user "ticket" and their ticket token as password.
                if (!allowed && stream is { Enabled: true, PaywallEnabled: true } && req.Action == "read" && user == TicketUser)
                {
                    var now = DateTime.UtcNow;
                    allowed = await db.Orders.AnyAsync(o => o.StreamId == stream.Id && o.AccessToken == pass
                        && o.Status == OrderStatus.Paid && o.AccessExpiresAt > now, ct);
                }
                break;

            default:
                allowed = false;
                break;
        }

        if (!allowed)
        {
            // An empty user is MediaMTX asking whether anonymous access is OK before it prompts for
            // credentials; that is routine and not worth a log line.
            if (user.Length > 0)
                logger.LogInformation("Denied {Action} on {Server}/'{Path}' for user '{User}' from {Ip} ({Protocol})",
                    req.Action, server.Name, req.Path, user, req.Ip, req.Protocol);
            return Results.Text("denied", statusCode: StatusCodes.Status401Unauthorized);
        }

        // Count the viewer for the viewer report; paying viewers by their ticket, others by IP address.
        if (req.Action == "read" && stream is not null)
            await reports.RecordVisitAsync(stream.Id, req.Ip, user == TicketUser ? pass : null, ct);

        return Results.Text("ok");
    }

    private static bool IsAllowed(StreamDefinition s, string action, string user, string pass)
    {
        bool PublishCreds() => CredentialGenerator.FixedTimeEquals(user, s.PublishUser)
                               && CredentialGenerator.FixedTimeEquals(pass, s.PublishPassword);
        bool ReadCreds() => CredentialGenerator.FixedTimeEquals(user, s.ReadUser)
                            && CredentialGenerator.FixedTimeEquals(pass, s.ReadPassword);

        return action switch
        {
            "publish" => s.SourceType == StreamSourceType.Publisher && PublishCreds(),
            // Recordings: a public live stream does not make its recordings public, and tickets are for live only.
            "playback" => ReadCreds() || PublishCreds(),
            // Publishers may always watch their own stream. A paywall overrides "public".
            _ => (s.PublicRead && !s.PaywallEnabled) || ReadCreds() || PublishCreds(),
        };
    }
}
