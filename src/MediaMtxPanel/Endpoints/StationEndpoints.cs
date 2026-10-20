using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Endpoints;

/// <summary>
/// Station contract version 1 (docs/station-contract.md in Build My TV Station): /healthz, and the
/// /api/v1/streams API on the first (default) media server that the service token gives access to.
/// </summary>
public static class StationEndpoints
{
    public const int ContractVersion = 1;
    public const string AppName = "mediamtx-panel";

    public record Health(string Status, string App, string Version, int Contract);

    public record CreateStreamRequest(string? Name, bool PublicRead);

    public record PublishUrls(string Rtmp, string Srt, string Rtsp);

    public record PlayUrls(string Hls, string Webrtc, string Rtsp, string Srt);

    public record StreamResource(
        int Id, string Name, bool PublicRead, PublishUrls? Publish, PlayUrls Play,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Warning = null);

    public static IEndpointRouteBuilder MapStationEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous and without secrets. Under a path base it answers at the root as well, because
        // UsePathBase leaves requests outside the base path alone.
        app.MapGet("/healthz", () => Results.Json(new Health("ok", AppName, BuildInfo.Version, ContractVersion)))
            .AllowAnonymous();

        var api = app.MapGroup(ServiceTokenAuthenticationHandler.ApiPrefix + "/streams")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(ServiceTokenAuthenticationHandler.SchemeName)
                .RequireRole(Roles.Admin))
            .DisableAntiforgery()
            // A JSON API: 401 and 404 must not be re-executed as the HTML not-found page.
            .WithMetadata(new SkipStatusCodePagesAttribute());

        api.MapGet("/", async (ClaimsPrincipal user, StreamService streams, StreamEndpointBuilder endpoints, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct) =>
        {
            var server = await DefaultServerAsync(dbFactory, ct);
            if (server is null)
                return Results.Ok(Array.Empty<StreamResource>());
            var list = await streams.ListAsync(user, ct);
            return Results.Ok(list.Where(s => s.ServerId == server.Id).Select(s => ToResource(endpoints, s)).ToList());
        });

        api.MapGet("/{id:int}", async (int id, ClaimsPrincipal user, StreamService streams, StreamEndpointBuilder endpoints, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct) =>
        {
            var stream = await GetOnDefaultServerAsync(id, user, streams, dbFactory, ct);
            return stream is null ? Results.NotFound() : Results.Ok(ToResource(endpoints, stream));
        });

        api.MapPost("/", async (CreateStreamRequest body, HttpContext http, ClaimsPrincipal user, StreamService streams, StreamEndpointBuilder endpoints,
            IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct) =>
        {
            var server = await DefaultServerAsync(dbFactory, ct);
            if (server is null)
                return Results.Problem("The panel has no media server yet; add one under Servers first.", statusCode: StatusCodes.Status409Conflict);

            var input = new StreamInput
            {
                Name = (body.Name ?? "").Trim(),
                ServerId = server.Id,
                SourceType = StreamSourceType.Publisher,
                PublicRead = body.PublicRead,
            };
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true))
                return Results.ValidationProblem(results
                    .GroupBy(r => JsonName(r.MemberNames.FirstOrDefault() ?? ""))
                    .ToDictionary(g => g.Key, g => g.Select(r => r.ErrorMessage ?? "Invalid value.").ToArray()));
            if (await streams.NameExistsAsync(server.Id, input.Name, ct: ct))
                return Results.Problem($"A stream named '{input.Name}' already exists.", statusCode: StatusCodes.Status409Conflict);

            var (stream, warning) = await streams.CreateAsync(user, input, ct);
            return Results.Created($"{http.Request.PathBase}{ServiceTokenAuthenticationHandler.ApiPrefix}/streams/{stream.Id}", ToResource(endpoints, stream, warning));
        });

        api.MapDelete("/{id:int}", async (int id, ClaimsPrincipal user, StreamService streams, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct) =>
        {
            if (await GetOnDefaultServerAsync(id, user, streams, dbFactory, ct) is null)
                return Results.NotFound();
            var (deleted, _) = await streams.DeleteAsync(user, id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }

    /// <summary>The first media server (lowest id), which the station contract works on.</summary>
    public static async Task<MediaServer?> DefaultServerAsync(IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Servers.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
    }

    private static async Task<StreamDefinition?> GetOnDefaultServerAsync(
        int id, ClaimsPrincipal user, StreamService streams, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct)
    {
        var server = await DefaultServerAsync(dbFactory, ct);
        var stream = server is null ? null : await streams.GetAsync(user, id, ct);
        return stream?.ServerId == server?.Id ? stream : null;
    }

    private static string JsonName(string member) =>
        member.Length == 0 ? "" : char.ToLowerInvariant(member[0]) + member[1..];

    /// <summary>The stream with its connection URLs; read URLs carry the read credentials unless the stream is public.</summary>
    public static StreamResource ToResource(StreamEndpointBuilder endpoints, StreamDefinition s, string? warning = null)
    {
        var server = s.Server ?? throw new InvalidOperationException("Load the stream together with its server.");
        var h = server.PublicHost;
        string Enc(string v) => Uri.EscapeDataString(v);

        PublishUrls? publish = s.SourceType != StreamSourceType.Publisher ? null : new(
            $"rtmp://{h}:{server.RtmpPort}/{s.Name}?user={Enc(s.PublishUser)}&pass={Enc(s.PublishPassword)}",
            $"srt://{h}:{server.SrtPort}?streamid=publish:{s.Name}:{s.PublishUser}:{s.PublishPassword}",
            $"rtsp://{Enc(s.PublishUser)}:{Enc(s.PublishPassword)}@{h}:{server.RtspPort}/{s.Name}");

        var creds = s.PublicRead ? "" : $"{Enc(s.ReadUser)}:{Enc(s.ReadPassword)}@";
        var play = new PlayUrls(
            StreamEndpointBuilder.WithUserInfo($"{endpoints.HlsBase(server)}/{s.Name}/index.m3u8", creds),
            StreamEndpointBuilder.WithUserInfo($"{endpoints.WebRtcBase(server)}/{s.Name}/", creds),
            $"rtsp://{creds}{h}:{server.RtspPort}/{s.Name}",
            $"srt://{h}:{server.SrtPort}?streamid=read:{s.Name}{(s.PublicRead ? "" : $":{s.ReadUser}:{s.ReadPassword}")}");

        return new StreamResource(s.Id, s.Name, s.PublicRead, publish, play, warning);
    }
}
