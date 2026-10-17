using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using MediaMtxPanel.Components;
using MediaMtxPanel.Components.Account;
using MediaMtxPanel.Data;
using MediaMtxPanel.Endpoints;
using MediaMtxPanel.Options;
using MediaMtxPanel.Services;
using MediaMtxPanel.Services.Localization;
using MediaMtxPanel.Services.Payments;
using MediaMtxPanel.Services.Setup;

// "admin-reset" and "station-info" run a command for deploy/mediamtx-panel-station.sh instead of the web server.
var stationCommand = StationCommands.Parse(args);
var builder = WebApplication.CreateBuilder(stationCommand is null ? args : args[1..]);
if (stationCommand is not null)
    StationCommands.ConfigureLogging(builder);

// Settings from the panel's Settings page; the web port has to be known before the server starts.
var settingsStore = new PanelSettingsStore(PanelSettingsStore.ResolvePath(builder.Configuration, builder.Environment.ContentRootPath));
builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton(WebPortInfo.Apply(builder, settingsStore.Current));
builder.Services.AddSingleton<TextLocalizer>();
builder.Services.AddLocalization();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
// Station contract: "Authorization: Bearer <Panel:ServiceToken>" on /api/v1 only; does nothing without a token.
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ServiceTokenAuthenticationHandler>(ServiceTokenAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // Accounts are created by an administrator, so there is no e-mail confirmation step.
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddErrorDescriber<LocalizedIdentityErrorDescriber>();

builder.Services.AddSingleton<MailService>();
builder.Services.AddSingleton<IEmailSender<ApplicationUser>>(sp => sp.GetRequiredService<MailService>());

builder.Services.Configure<MediaMtxOptions>(builder.Configuration.GetSection(MediaMtxOptions.Section));
builder.Services.Configure<PanelOptions>(builder.Configuration.GetSection(PanelOptions.Section));
builder.Services.AddHttpClient("mediamtx");
builder.Services.AddSingleton<MediaMtxClientFactory>();
builder.Services.AddScoped<StreamService>();
builder.Services.AddScoped<ServerService>();
builder.Services.AddSingleton<SshServerSetup>();
builder.Services.AddSingleton<LiveStatusReader>();
builder.Services.AddSingleton<ServerMetricsReader>();
builder.Services.AddHttpClient("metrics");
builder.Services.AddHttpClient("payments", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<IPaymentGateway, MollieGateway>();
builder.Services.AddSingleton<IPaymentGateway, PayPalGateway>();
if (builder.Configuration.GetValue("Panel:EnableTestPayments", false))
    builder.Services.AddSingleton<IPaymentGateway, TestGateway>();
builder.Services.AddScoped<PaywallService>();
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddSingleton<StreamEndpointBuilder>();
builder.Services.AddSingleton<MediaMtxStatus>();
builder.Services.AddSingleton<MediaMtxSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaMtxSyncService>());
builder.Services.AddHttpClient("github", c =>
{
    c.BaseAddress = new Uri("https://api.github.com/");
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd($"MediaMtxPanel/{BuildInfo.Version}");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});
builder.Services.AddSingleton<UpdateChecker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateChecker>());

// Keep the cookie encryption keys next to the database so logins survive container restarts.
var keysDir = builder.Configuration["Panel:DataProtectionKeysPath"];
if (!string.IsNullOrEmpty(keysDir))
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDir));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    // Panel:TrustedProxies: only these addresses may set X-Forwarded-*; without it any address may (as before).
    var trusted = builder.Configuration["Panel:TrustedProxies"];
    if (!string.IsNullOrWhiteSpace(trusted))
    {
        options.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;
        foreach (var entry in trusted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (System.Net.IPNetwork.TryParse(entry, out var network) && entry.Contains('/'))
                options.KnownIPNetworks.Add(network);
            else if (IPAddress.TryParse(entry, out var address))
                options.KnownProxies.Add(address);
            else
                throw new InvalidOperationException($"Panel:TrustedProxies: '{entry}' is not an IP address or CIDR.");
        }
    }
});

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services);

if (stationCommand is not null)
{
    Environment.ExitCode = await StationCommands.RunAsync(app, stationCommand);
    return;
}

if (app.Configuration["Panel:ServiceToken"] is { Length: > 0 and < ServiceTokenAuthenticationHandler.MinimumLength })
    app.Logger.LogWarning("Panel:ServiceToken is shorter than {Length} characters and is ignored", ServiceTokenAuthenticationHandler.MinimumLength);

app.UseForwardedHeaders();

// Panel:PathBase (e.g. /channel1): pages, assets, APIs, redirects and cookies live under it. The proxy
// forwards the full path. Requests outside it (/healthz, the MediaMTX auth hook on 127.0.0.1) still work.
var pathBase = PanelOptions.NormalizePathBase(app.Configuration["Panel:PathBase"]);
if (pathBase.Length > 0)
{
    app.Use((context, next) =>
    {
        if (context.Request.Path.Equals(pathBase, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Redirect($"{context.Request.PathBase}{pathBase}/{context.Request.QueryString}");
            return Task.CompletedTask;
        }
        return next(context);
    });
    app.UsePathBase(pathBase);
    // Routing and authentication must run after UsePathBase, so that routes and cookie paths see the base path.
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
}

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (app.Configuration.GetValue("Panel:HttpsRedirect", false))
    app.UseHttpsRedirection();

// Language: the visitor's own choice (cookie), otherwise the default language from Settings.
var supportedCultures = TextLocalizer.Codes;
app.UseRequestLocalization(o =>
{
    o.SetDefaultCulture(TextLocalizer.DefaultLanguage)
        .AddSupportedCultures(supportedCultures)
        .AddSupportedUICultures(supportedCultures);
    o.RequestCultureProviders =
    [
        new CookieRequestCultureProvider(),
        new CustomRequestCultureProvider(_ =>
            Task.FromResult<ProviderCultureResult?>(new(settingsStore.Current.DefaultLanguage))),
    ];
});

app.UseRequiredPasswordChange();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();
app.MapMediaMtxAuth();
app.MapStationEndpoints();
app.MapPaymentEndpoints();
app.MapLanguageEndpoint();

app.Run();
