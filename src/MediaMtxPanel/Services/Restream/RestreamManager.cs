using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using MediaMtxPanel.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Services.Restream;

public enum RestreamState
{
    /// <summary>The target is switched off.</summary>
    Off,
    /// <summary>Enabled, waiting for the stream to go live.</summary>
    Waiting,
    /// <summary>ffmpeg started and is connecting to the stream and the platform.</summary>
    Connecting,
    /// <summary>Video is flowing to the platform.</summary>
    Live,
    /// <summary>ffmpeg stopped with an error; the panel tries again shortly.</summary>
    Retrying,
}

public record RestreamStatus(RestreamState State, string? Error = null, DateTime? LiveSince = null, double? BitrateKbps = null, int Attempts = 0);

/// <summary>
/// Runs one ffmpeg process per enabled restream target whose stream is live: it reads the stream over
/// RTSP from its MediaMTX server (with the stream's viewer credentials) and pushes it to the target over
/// RTMP(S). Reconciles every few seconds and right after a target changes.
/// </summary>
public partial class RestreamManager(
    IServiceProvider services,
    IDataProtectionProvider dataProtection,
    IConfiguration configuration,
    ILogger<RestreamManager> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<int, Worker> workers = new();
    private readonly ConcurrentDictionary<int, RestreamStatus> statuses = new();
    private readonly SemaphoreSlim wake = new(0);

    private string FfmpegPath => configuration["Restream:FfmpegPath"] is { Length: > 0 } p ? p : "ffmpeg";

    /// <summary>False when ffmpeg could not be started; restreaming then does nothing.</summary>
    public bool? FfmpegAvailable { get; private set; }

    public RestreamStatus GetStatus(RestreamTarget t) =>
        !t.Enabled ? new(RestreamState.Off) : statuses.GetValueOrDefault(t.Id) ?? new(RestreamState.Waiting);

    /// <summary>Reconcile now instead of at the next tick, e.g. after a target was changed.</summary>
    public void Wake()
    {
        if (wake.CurrentCount == 0)
            wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FfmpegAvailable = await CheckFfmpegAsync(stoppingToken);
        if (FfmpegAvailable == false)
            logger.LogWarning("ffmpeg ({Path}) was not found; restreaming is unavailable until it is installed", FfmpegPath);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Restream reconcile failed");
                }
                await wake.WaitAsync(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var id in workers.Keys)
                Stop(id);
        }
    }

    private async Task<bool> CheckFfmpegAsync(CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(FfmpegPath, "-hide_banner -version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var liveReader = scope.ServiceProvider.GetRequiredService<LiveStatusReader>();

        List<RestreamTarget> targets;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            targets = await db.RestreamTargets.AsNoTracking()
                .Include(t => t.Stream).ThenInclude(s => s!.Server)
                .Where(t => t.Enabled && t.Stream!.Enabled && t.Stream.Server!.Enabled)
                .ToListAsync(ct);

        var snapshot = targets.Count == 0
            ? new LiveSnapshot()
            : await liveReader.ReadAsync(targets.Select(t => t.Stream!.Server!), includeInfo: false, ct);

        var wanted = new Dictionary<int, Job>();
        var problems = new Dictionary<int, string>();
        foreach (var t in targets)
        {
            var s = t.Stream!;
            // A pull source "on demand" only starts when someone reads it, so the restream itself is that reader.
            var live = snapshot.Find(s) is { IsLive: true } || (s.SourceType == StreamSourceType.Pull && s.SourceOnDemand);
            if (!live)
                continue;
            if (FfmpegAvailable == false)
                problems[t.Id] = FfmpegMissing;
            else if (BuildJob(t, out var problem) is { } job)
                wanted[t.Id] = job;
            else if (problem is not null)
                problems[t.Id] = problem;
        }

        foreach (var (id, worker) in workers)
            if (!wanted.TryGetValue(id, out var job) || job != worker.Job)
                Stop(id);

        foreach (var (id, job) in wanted)
            if (!workers.ContainsKey(id))
                Start(id, job);

        // Forget the state of targets that are no longer running, so they show "waiting" again.
        foreach (var id in statuses.Keys)
            if (!workers.ContainsKey(id))
                statuses.TryRemove(id, out _);
        foreach (var (id, problem) in problems)
            statuses[id] = new(RestreamState.Retrying, problem);
    }

    public const string FfmpegMissing = "ffmpeg is not installed on the panel server.";
    public const string KeyUnreadable = "The stored stream key cannot be decrypted (the panel's encryption keys changed). Enter the key again.";

    private Job? BuildJob(RestreamTarget t, out string? problem)
    {
        problem = null;
        var s = t.Stream!;
        var server = s.Server!;
        string key = "";
        if (!string.IsNullOrEmpty(t.StreamKeyProtected))
        {
            try
            {
                key = dataProtection.CreateProtector(RestreamService.ProtectorPurpose).Unprotect(t.StreamKeyProtected);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                problem = KeyUnreadable;
                return null;
            }
        }

        var baseUrl = t.ServerUrl ?? RestreamPlatforms.Get(t.Platform).DefaultUrl;
        if (string.IsNullOrEmpty(baseUrl))
            return null;
        var output = key.Length == 0 ? baseUrl : $"{baseUrl.TrimEnd('/')}/{key}";

        // The panel reads like any other viewer. The publish credentials may read too, and pull streams may have no viewer user.
        var (user, pass) = s.ReadUser.Length > 0 ? (s.ReadUser, s.ReadPassword) : (s.PublishUser, s.PublishPassword);
        var host = Uri.TryCreate(server.ApiUrl, UriKind.Absolute, out var api) ? api.Host : server.PublicHost;
        var creds = user.Length > 0 ? $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@" : "";
        var input = $"rtsp://{creds}{host}:{server.RtspPort}/{s.Name}";

        return new Job(input, output, t.TranscodeVideo, t.VideoBitrateKbps, t.AudioBitrateKbps,
            [key, pass, Uri.EscapeDataString(pass)]);
    }

    private void Start(int id, Job job)
    {
        var worker = new Worker(job, new CancellationTokenSource());
        workers[id] = worker;
        statuses[id] = new(RestreamState.Connecting);
        worker.Task = Task.Run(() => RunAsync(id, worker));
    }

    private void Stop(int id)
    {
        if (workers.TryRemove(id, out var worker))
            worker.Cts.Cancel();
        statuses.TryRemove(id, out _);
    }

    private async Task RunAsync(int id, Worker worker)
    {
        var ct = worker.Cts.Token;
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            var started = DateTime.UtcNow;
            string? error;
            try
            {
                error = await RunFfmpegAsync(id, worker.Job, attempt, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (ct.IsCancellationRequested)
                break;

            // A run that lasted a while was a success that ended (e.g. the encoder stopped); start counting again.
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(1))
                attempt = 1;
            var delay = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, attempt - 1)));
            statuses[id] = new(RestreamState.Retrying, error ?? "ffmpeg stopped.", Attempts: attempt);
            logger.LogWarning("Restream target {Id} stopped: {Error}; retrying in {Delay}s", id, error, delay.TotalSeconds);
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            Wake();
        }
    }

    /// <summary>Runs ffmpeg until it exits; returns its last error lines (secrets removed).</summary>
    private async Task<string?> RunFfmpegAsync(int id, Job job, int attempt, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in job.Arguments())
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var errors = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            lock (errors)
            {
                var text = job.Redact(LogPrefix().Replace(e.Data, "").Trim());
                if (!errors.Contains(text))
                    errors.Enqueue(text);
                while (errors.Count > 2)
                    errors.Dequeue();
            }
        };
        DateTime? liveSince = null;
        process.OutputDataReceived += (_, e) =>
        {
            // -progress writes key=value lines; "progress=continue" ends each block.
            if (e.Data is not { } line)
                return;
            if (line.StartsWith("bitrate=", StringComparison.Ordinal))
            {
                // e.g. "bitrate=6921.5kbits/s" or "bitrate=N/A"
                var value = line["bitrate=".Length..].Replace("kbits/s", "").Trim();
                double? kbps = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
                liveSince ??= DateTime.UtcNow;
                statuses[id] = new(RestreamState.Live, LiveSince: liveSince, BitrateKbps: kbps, Attempts: attempt);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        process.WaitForExit();
        lock (errors)
            return errors.Count > 0
                ? string.Join(" ", errors)
                : $"ffmpeg exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}.";
    }

    // ffmpeg prefixes errors with the component, e.g. "[out#0/flv @ 0x5592...] ".
    [GeneratedRegex(@"^(\[[^\]]*\]\s*)+")]
    private static partial Regex LogPrefix();

    private sealed class Worker(Job job, CancellationTokenSource cts)
    {
        public Job Job { get; } = job;
        public CancellationTokenSource Cts { get; } = cts;
        public Task? Task { get; set; }
    }

    /// <summary>Everything one ffmpeg run depends on; a change restarts it.</summary>
    private sealed record Job(string Input, string Output, bool TranscodeVideo, int VideoKbps, int AudioKbps, string[] Secrets)
    {
        public bool Equals(Job? other) => other is not null && Input == other.Input && Output == other.Output
            && TranscodeVideo == other.TranscodeVideo && VideoKbps == other.VideoKbps && AudioKbps == other.AudioKbps;

        public override int GetHashCode() => HashCode.Combine(Input, Output, TranscodeVideo, VideoKbps, AudioKbps);

        public IEnumerable<string> Arguments()
        {
            string[] head = ["-hide_banner", "-nostdin", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-stats_period", "2",
                "-rtsp_transport", "tcp", "-i", Input, "-map", "0:v:0?", "-map", "0:a:0?"];
            string[] video = TranscodeVideo
                ? ["-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                   "-b:v", $"{VideoKbps}k", "-maxrate", $"{VideoKbps}k", "-bufsize", $"{VideoKbps * 2}k",
                   "-force_key_frames", "expr:gte(t,n_forced*2)"]
                : ["-c:v", "copy"];
            // Platforms want AAC; WebRTC publishers send Opus. Re-encoding audio costs little.
            string[] audio = ["-c:a", "aac", "-b:a", $"{AudioKbps}k", "-ar", "48000"];
            string[] tail = ["-f", "flv", "-flvflags", "no_duration_filesize", Output];
            return [.. head, .. video, .. audio, .. tail];
        }

        public string Redact(string text)
        {
            foreach (var secret in Secrets.Where(s => s.Length >= 4))
                text = text.Replace(secret, "••••", StringComparison.Ordinal);
            return text;
        }
    }
}
