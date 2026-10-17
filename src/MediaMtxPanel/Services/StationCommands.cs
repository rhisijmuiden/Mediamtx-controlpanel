using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using MediaMtxPanel.Data;
using MediaMtxPanel.Endpoints;
using MediaMtxPanel.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Services;

/// <summary>
/// Command-line commands for deploy/mediamtx-panel-station.sh, run with the service's configuration
/// instead of starting the web server:
/// <list type="bullet">
/// <item><c>admin-reset</c>: gives the administrator a new one-time password (printed on stdout) that must be changed at the next login.</item>
/// <item><c>station-info</c>: prints the station.json of the station contract on stdout.</item>
/// </list>
/// Logging goes to stderr, so stdout carries only the result.
/// </summary>
public static class StationCommands
{
    public const string AdminReset = "admin-reset";
    public const string StationInfo = "station-info";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The command in <paramref name="args"/>, or null for a normal start of the panel.</summary>
    public static string? Parse(string[] args) =>
        args is [var first, ..] && first is AdminReset or StationInfo ? first : null;

    public static void ConfigureLogging(WebApplicationBuilder builder)
    {
        // Only warnings and errors (e.g. no "migrations applied" lines), and all of them on stderr.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Warning" });
        builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    }

    public static async Task<int> RunAsync(WebApplication app, string command)
    {
        try
        {
            return command switch
            {
                AdminReset => await ResetAdminAsync(app.Services),
                _ => await PrintStationInfoAsync(app.Services),
            };
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"{command} failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The administrator the station logs in as: the one with Panel:AdminEmail, otherwise the oldest administrator.</summary>
    private static async Task<ApplicationUser?> FindAdminAsync(UserManager<ApplicationUser> users, PanelOptions panel) =>
        (await users.GetUsersInRoleAsync(Roles.Admin))
            .OrderByDescending(u => string.Equals(u.Email, panel.AdminEmail, StringComparison.OrdinalIgnoreCase))
            .ThenBy(u => u.CreatedAt)
            .FirstOrDefault();

    private static async Task<int> ResetAdminAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await FindAdminAsync(users, sp.GetRequiredService<IOptions<PanelOptions>>().Value);
        if (admin is null)
        {
            await Console.Error.WriteLineAsync("There is no administrator.");
            return 1;
        }

        // Same password shape as the first administrator's generated password (see DbInitializer).
        var password = CredentialGenerator.Password(16) + "!a1";
        var result = await sp.GetRequiredService<UserAdminService>().ResetPasswordAsync(admin.Id, password);
        if (!result.Succeeded)
        {
            await Console.Error.WriteLineAsync("Could not reset the password: " + string.Join("; ", result.Errors.Select(e => e.Description)));
            return 1;
        }

        admin = await users.FindByIdAsync(admin.Id) ?? admin;
        if (!(await users.GetClaimsAsync(admin)).Any(c => c.Type == PasswordChange.ClaimType))
            await users.AddClaimAsync(admin, new Claim(PasswordChange.ClaimType, "1"));
        // A locked-out administrator could not use the new password.
        await users.SetLockoutEndDateAsync(admin, null);
        await users.ResetAccessFailedCountAsync(admin);

        Console.Out.WriteLine(password);
        return 0;
    }

    private static async Task<int> PrintStationInfoAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var panel = sp.GetRequiredService<IOptions<PanelOptions>>().Value;
        var mtx = sp.GetRequiredService<IOptions<MediaMtxOptions>>().Value;
        var web = sp.GetRequiredService<WebPortInfo>();
        var admin = await FindAdminAsync(sp.GetRequiredService<UserManager<ApplicationUser>>(), panel);
        var server = await StationEndpoints.DefaultServerAsync(sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>());

        // The running service holds the port, so this process cannot test it; take the chosen port as the service does.
        var port = web.ChosenPort is >= 1 and <= 65535 && !web.InContainer ? web.ChosenPort.Value : web.ConfiguredPort;
        var basePath = PanelOptions.NormalizePathBase(panel.PathBase);
        var info = new
        {
            contract = StationEndpoints.ContractVersion,
            app = StationEndpoints.AppName,
            version = BuildInfo.Version,
            port,
            basePath = basePath.Length == 0 ? "/" : basePath,
            adminUrl = $"http://{LocalIPv4() ?? "127.0.0.1"}:{port}{basePath}/",
            adminUser = admin?.Email ?? panel.AdminEmail,
            apiBase = basePath + ServiceTokenAuthenticationHandler.ApiPrefix,
            serviceToken = ServiceTokenAuthenticationHandler.IsConfigured(panel),
            ports = new Dictionary<string, int>
            {
                ["rtmp"] = server?.RtmpPort ?? mtx.RtmpPort,
                ["rtsp"] = server?.RtspPort ?? mtx.RtspPort,
                ["srt"] = server?.SrtPort ?? mtx.SrtPort,
                ["hls"] = server?.HlsPort ?? mtx.HlsPort,
                ["webrtc"] = server?.WebRtcPort ?? mtx.WebRtcPort,
            },
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(info, Json));
        return 0;
    }

    /// <summary>First non-loopback IPv4 address of an interface that is up (container interfaces may report "unknown").</summary>
    private static string? LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))
            ?.ToString();
}
