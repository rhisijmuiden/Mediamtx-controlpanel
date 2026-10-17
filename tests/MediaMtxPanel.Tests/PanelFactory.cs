using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace MediaMtxPanel.Tests;

/// <summary>
/// The panel on a TestServer with its own SQLite database in a temporary folder. The first media
/// server points at a port nothing listens on, so pushes to MediaMTX fail with a warning.
/// </summary>
public sealed class PanelFactory(IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public const string Token = "test-service-token-0123456789abcdefghijklmnop";

    private readonly string dataDir = Directory.CreateTempSubdirectory("mediamtx-panel-tests-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={Path.Combine(dataDir, "panel.db")};Cache=Shared");
        builder.UseSetting("Panel:DataProtectionKeysPath", Path.Combine(dataDir, "keys"));
        builder.UseSetting("Panel:AdminPassword", "Test-Password-123");
        builder.UseSetting("Panel:UpdateRepository", "");
        builder.UseSetting("MediaMtx:ApiUrl", "http://127.0.0.1:9");
        builder.UseSetting("MediaMtx:PublicHost", "stream.test");
        builder.UseSetting("MediaMtx:SyncIntervalSeconds", "3600");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);
    }

    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public HttpClient TokenClient(string token = Token)
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { }
    }
}
