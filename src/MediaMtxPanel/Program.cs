using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MediaMtxPanel.Components;
using MediaMtxPanel.Components.Account;
using MediaMtxPanel.Data;
using MediaMtxPanel.Endpoints;
using MediaMtxPanel.Options;
using MediaMtxPanel.Services;

var builder = WebApplication.CreateBuilder(args);

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
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

builder.Services.Configure<MediaMtxOptions>(builder.Configuration.GetSection(MediaMtxOptions.Section));
builder.Services.Configure<PanelOptions>(builder.Configuration.GetSection(PanelOptions.Section));
builder.Services.AddHttpClient("mediamtx");
builder.Services.AddSingleton<MediaMtxClientFactory>();
builder.Services.AddScoped<StreamService>();
builder.Services.AddScoped<ServerService>();
builder.Services.AddSingleton<LiveStatusReader>();
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddSingleton<StreamEndpointBuilder>();
builder.Services.AddSingleton<MediaMtxStatus>();
builder.Services.AddSingleton<MediaMtxSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaMtxSyncService>());

// Keep the cookie encryption keys next to the database so logins survive container restarts.
var keysDir = builder.Configuration["Panel:DataProtectionKeysPath"];
if (!string.IsNullOrEmpty(keysDir))
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDir));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services);

app.UseForwardedHeaders();
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

app.UseRequiredPasswordChange();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();
app.MapMediaMtxAuth();

app.Run();
