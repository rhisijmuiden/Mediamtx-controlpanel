using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using MediaMtxPanel.Options;
using Microsoft.Extensions.Options;

namespace MediaMtxPanel.Services;

/// <summary>Version, commit and build date of the running panel, from the assembly attributes set at build time.</summary>
public static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;
    private static readonly string Informational =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>e.g. 1.1.0</summary>
    public static string Version { get; } = Informational.Split('+')[0];

    /// <summary>Full git commit the panel was built from, or null when unknown.</summary>
    public static string? Commit { get; } = Informational.Contains('+') ? Informational.Split('+')[1] : null;

    public static string? ShortCommit => Commit is { Length: >= 7 } c ? c[..7] : Commit;

    public static DateTimeOffset? BuildDate { get; } =
        DateTimeOffset.TryParse(Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
}

public record UpdateStatus
{
    public DateTimeOffset? CheckedAt { get; init; }
    public bool UpdateAvailable { get; init; }

    /// <summary>Latest release tag, or the short commit of the branch when the repository has no releases.</summary>
    public string? Latest { get; init; }

    /// <summary>Number of commits on the branch that this build does not have (only without releases).</summary>
    public int? CommitsBehind { get; init; }

    public string? Url { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Checks GitHub for a newer version: the latest release when the repository publishes releases,
/// otherwise the newest commit on the branch the panel is installed from. Runs at startup and every
/// 24 hours (unless switched off under Settings), and on demand from the Info panel.
/// </summary>
public sealed class UpdateChecker(
    IHttpClientFactory httpFactory,
    IOptions<PanelOptions> options,
    PanelSettingsStore settings,
    ILogger<UpdateChecker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim gate = new(1, 1);

    public UpdateStatus Status { get; private set; } = new();
    public string Repository => options.Value.UpdateRepository;
    public string Branch => options.Value.UpdateBranch;
    public bool Checking { get; private set; }
    public event Action? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            var due = Status.CheckedAt is not { } last || DateTimeOffset.UtcNow - last >= Interval;
            if (due && settings.Current.AutoUpdateCheck && !string.IsNullOrWhiteSpace(Repository))
                await CheckAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct))
            return Status;
        Checking = true;
        Changed?.Invoke();
        try
        {
            Status = await QueryAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Update check failed: {Message}", ex.Message);
            Status = Status with { CheckedAt = DateTimeOffset.UtcNow, Error = ex.Message };
        }
        finally
        {
            Checking = false;
            gate.Release();
        }
        Changed?.Invoke();
        return Status;
    }

    private async Task<UpdateStatus> QueryAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient("github");
        var repo = Repository.Trim('/');
        var now = DateTimeOffset.UtcNow;

        using (var release = await http.GetAsync($"repos/{repo}/releases/latest", ct))
        {
            if (release.IsSuccessStatusCode)
            {
                var json = await ReadAsync(release, ct);
                var tag = json.GetProperty("tag_name").GetString() ?? "";
                var newer = System.Version.TryParse(tag.TrimStart('v', 'V'), out var latest)
                            && System.Version.TryParse(BuildInfo.Version, out var mine) && latest > mine;
                return new UpdateStatus
                {
                    CheckedAt = now, Latest = tag, UpdateAvailable = newer,
                    Url = json.GetProperty("html_url").GetString(),
                };
            }
            if (release.StatusCode != HttpStatusCode.NotFound)
                release.EnsureSuccessStatusCode();
        }

        // No releases: compare with the branch the installer builds from.
        var branchUrl = $"https://github.com/{repo}/commits/{Branch}";
        if (BuildInfo.Commit is { } commit)
        {
            using var compare = await http.GetAsync($"repos/{repo}/compare/{commit}...{Branch}", ct);
            if (compare.IsSuccessStatusCode)
            {
                var json = await ReadAsync(compare, ct);
                var ahead = json.GetProperty("ahead_by").GetInt32();
                var head = json.GetProperty("commits").EnumerateArray().LastOrDefault();
                var sha = head.ValueKind == JsonValueKind.Object ? head.GetProperty("sha").GetString() : commit;
                return new UpdateStatus
                {
                    CheckedAt = now, UpdateAvailable = ahead > 0, CommitsBehind = ahead,
                    Latest = sha?[..Math.Min(7, sha.Length)], Url = ahead > 0 ? json.GetProperty("html_url").GetString() : branchUrl,
                };
            }
            // 404: the build's commit is not on GitHub (local changes); fall back to the build date.
        }

        using var latestCommit = await http.GetAsync($"repos/{repo}/commits/{Branch}", ct);
        latestCommit.EnsureSuccessStatusCode();
        var c = await ReadAsync(latestCommit, ct);
        var latestSha = c.GetProperty("sha").GetString() ?? "";
        var date = c.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset();
        return new UpdateStatus
        {
            CheckedAt = now, Latest = latestSha[..Math.Min(7, latestSha.Length)], Url = branchUrl,
            UpdateAvailable = latestSha != BuildInfo.Commit && BuildInfo.BuildDate is { } built && date > built,
        };
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return (await JsonDocument.ParseAsync(stream, cancellationToken: ct)).RootElement;
    }
}
