using MediaMtxPanel.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        var db = sp.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        await SeedFirstServerAsync(db, sp.GetRequiredService<IOptions<MediaMtxOptions>>().Value, logger);

        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        if ((await userManager.GetUsersInRoleAsync(Roles.Admin)).Count > 0)
            return;

        var panel = sp.GetRequiredService<IOptions<PanelOptions>>().Value;
        var password = panel.AdminPassword;
        var generated = string.IsNullOrWhiteSpace(password);
        if (generated)
            password = CredentialGenerator.Password(16) + "!a1";

        var admin = await userManager.FindByEmailAsync(panel.AdminEmail);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                UserName = panel.AdminEmail,
                Email = panel.AdminEmail,
                EmailConfirmed = true,
                DisplayName = "Administrator",
            };
            var result = await userManager.CreateAsync(admin, password);
            if (!result.Succeeded)
                throw new InvalidOperationException("Could not create the administrator: " +
                    string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        await userManager.AddToRoleAsync(admin, Roles.Admin);

        if (generated)
            logger.LogWarning("Created administrator {Email} with generated password: {Password}  (set Panel__AdminPassword to choose one)",
                panel.AdminEmail, password);
        else
            logger.LogInformation("Created administrator {Email}", panel.AdminEmail);
    }

    private static async Task SeedFirstServerAsync(ApplicationDbContext db, MediaMtxOptions o, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(o.ApiUrl) || await db.Servers.AnyAsync())
            return;

        db.Servers.Add(new MediaServer
        {
            Name = o.ServerName,
            ApiUrl = o.ApiUrl.TrimEnd('/'),
            ApiUser = o.ApiUser,
            ApiPassword = string.IsNullOrEmpty(o.ApiPassword) ? CredentialGenerator.Password(24) : o.ApiPassword,
            HookSecret = string.IsNullOrEmpty(o.AuthHookSecret) ? CredentialGenerator.Password(32) : o.AuthHookSecret,
            PublicHost = o.PublicHost,
            RtspPort = o.RtspPort,
            RtmpPort = o.RtmpPort,
            SrtPort = o.SrtPort,
            HlsPort = o.HlsPort,
            WebRtcPort = o.WebRtcPort,
            HttpsUrls = o.HttpsUrls,
        });
        await db.SaveChangesAsync();
        logger.LogInformation("Created server {Name} ({Url}) from configuration", o.ServerName, o.ApiUrl);
    }
}
