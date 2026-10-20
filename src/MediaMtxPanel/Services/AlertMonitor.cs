using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaMtxPanel.Data;
using MediaMtxPanel.Services.Localization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MediaMtxPanel.Services;

public enum AlertKind { ServerDown, ServerUp, StreamDown, StreamUp, Test }

public record AlertEvent(AlertKind Kind, string Title, string Message, int? ServerId, string? ServerName,
    int? StreamId, string? StreamName, TimeSpan? Downtime, DateTime At)
{
    public bool IsProblem => Kind is AlertKind.ServerDown or AlertKind.StreamDown;

    public string EventName => Kind switch
    {
        AlertKind.ServerDown => "server.down",
        AlertKind.ServerUp => "server.up",
        AlertKind.StreamDown => "stream.down",
        AlertKind.StreamUp => "stream.up",
        _ => "test",
    };
}

/// <summary>An alert that went out, with the channels it reached; shown under Settings → Alerts.</summary>
public record AlertLogEntry(AlertEvent Event, bool MailSent, bool WebhookSent, string? Error);

/// <summary>Something that is down right now (whether or not the alert went out yet).</summary>
public record DownItem(string Name, string? ServerName, bool IsServer, DateTime Since, bool Alerted);

/// <summary>
/// Checks every enabled MediaMTX server and its streams every 15 seconds. When a server cannot be reached,
/// or a stream that was live stops, for longer than the grace time, it sends a mail and/or webhook as set
/// under Settings → Alerts; and another one when it is back. State is kept in memory: after a restart
/// of the panel, only streams that are seen live again are watched.
/// </summary>
public sealed class AlertMonitor(
    IServiceProvider services,
    PanelSettingsStore store,
    MailService mailer,
    MediaMtxStatus status,
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtection,
    TextLocalizer L,
    ILogger<AlertMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);
    private const int HistorySize = 50;

    private sealed class Tracked
    {
        public required string Name;
        public string? ServerName;
        public bool Up;
        public bool EverUp;
        public DateTime Since;
        public bool DownAlerted;
    }

    private readonly ConcurrentDictionary<int, Tracked> servers = new();
    private readonly ConcurrentDictionary<int, Tracked> streams = new();
    private readonly ConcurrentQueue<AlertLogEntry> history = new();

    private IDataProtector Protector => dataProtection.CreateProtector("MediaMtxPanel.WebhookSecret.v1");

    /// <summary>Raised after an alert was sent, so an open Alerts page can refresh.</summary>
    public event Action? Changed;

    public IReadOnlyList<AlertLogEntry> History => history.Reverse().ToList();

    public DateTime? LastCheck { get; private set; }

    public IReadOnlyList<DownItem> Down =>
        servers.Values.Where(t => !t.Up).Select(t => new DownItem(t.Name, null, true, t.Since, t.DownAlerted))
            .Concat(streams.Values.Where(t => !t.Up && t.EverUp).Select(t => new DownItem(t.Name, t.ServerName, false, t.Since, t.DownAlerted)))
            .OrderBy(d => d.Since).ToList();

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : Protector.Protect(secret);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        // Give Kestrel a moment to start listening: MediaMTX calls back into the panel to authorize API requests.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        do
        {
            try
            {
                if (store.Current.Alerts is { Enabled: true } settings)
                    await CheckAsync(settings, stoppingToken);
                else
                {
                    servers.Clear();
                    streams.Clear();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Monitoring check failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAsync(AlertSettings settings, CancellationToken ct)
    {
        List<MediaServer> list;
        using (var scope = services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            list = await db.Servers.AsNoTracking().Include(s => s.Streams).Where(s => s.Enabled).ToListAsync(ct);
        }
        var clients = services.GetRequiredService<MediaMtxClientFactory>();

        var results = await Task.WhenAll(list.Select(async server =>
        {
            try
            {
                var mtx = clients.Create(server);
                var info = await mtx.GetInfoAsync(ct);
                var paths = await mtx.ListPathsAsync(ct);
                status.Report(server.Id, true, info?.Version, null);
                return (server, paths: (Dictionary<string, MtxPath>?)paths.ToDictionary(p => p.Name));
            }
            catch (Exception ex) when (ex is HttpRequestException or MediaMtxException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                status.Report(server.Id, false, null, ex.Message);
                return (server, paths: (Dictionary<string, MtxPath>?)null);
            }
        }));

        var now = DateTime.UtcNow;
        var grace = TimeSpan.FromSeconds(Math.Clamp(settings.GraceSeconds, 0, 3600));
        var events = new List<AlertEvent>();
        var seenServers = new HashSet<int>();
        var seenStreams = new HashSet<int>();

        foreach (var (server, paths) in results)
        {
            seenServers.Add(server.Id);
            var serverUp = paths is not null;
            if (settings.WatchServers)
                Evaluate(servers, server.Id, server.Name, null, serverUp, now, grace, events, settings, isServer: true, server.Id, null);
            else
                servers.TryRemove(server.Id, out _);

            // While a server is down its streams are not judged: the server alert covers them.
            if (!settings.WatchStreams || paths is null)
            {
                if (paths is null)
                    foreach (var s in server.Streams)
                        seenStreams.Add(s.Id);
                continue;
            }

            foreach (var stream in server.Streams.Where(s => IsWatched(s, settings)))
            {
                seenStreams.Add(stream.Id);
                var live = paths.TryGetValue(stream.Name, out var path) && path.IsLive;
                var name = string.IsNullOrWhiteSpace(stream.DisplayName) ? stream.Name : stream.DisplayName;
                Evaluate(streams, stream.Id, name, server.Name, live, now, grace, events, settings, isServer: false, server.Id, stream.Id);
            }
        }

        // Servers and streams that were removed, disabled or muted are forgotten.
        foreach (var id in servers.Keys.Where(id => !seenServers.Contains(id)))
            servers.TryRemove(id, out _);
        foreach (var id in streams.Keys.Where(id => !seenStreams.Contains(id)))
            streams.TryRemove(id, out _);

        LastCheck = now;
        foreach (var e in events)
            await SendAsync(e, settings, ct);
        Changed?.Invoke();
    }

    private static bool IsWatched(StreamDefinition s, AlertSettings settings) =>
        s.Enabled
        && !settings.MutedStreamIds.Contains(s.Id)
        // An on-demand pull stream is only live while someone watches it, so "offline" means nothing there.
        && !(s.SourceType == StreamSourceType.Pull && s.SourceOnDemand);

    private void Evaluate(ConcurrentDictionary<int, Tracked> table, int id, string name, string? serverName, bool up,
        DateTime now, TimeSpan grace, List<AlertEvent> events, AlertSettings settings, bool isServer, int serverId, int? streamId)
    {
        if (!table.TryGetValue(id, out var t))
        {
            table[id] = new Tracked { Name = name, ServerName = serverName, Up = up, EverUp = up, Since = now };
            t = table[id];
        }
        t.Name = name;
        t.ServerName = serverName;

        if (up && !t.Up)
        {
            if (t.DownAlerted && settings.NotifyRecovery)
                events.Add(Build(isServer ? AlertKind.ServerUp : AlertKind.StreamUp, t, serverId, streamId, now - t.Since, now));
            (t.Up, t.EverUp, t.Since, t.DownAlerted) = (true, true, now, false);
        }
        else if (!up && t.Up)
        {
            (t.Up, t.Since, t.DownAlerted) = (false, now, false);
        }

        // A server that is unreachable from the start counts; a stream only after it was seen live.
        if (!t.Up && !t.DownAlerted && (isServer || t.EverUp) && now - t.Since >= grace)
        {
            events.Add(Build(isServer ? AlertKind.ServerDown : AlertKind.StreamDown, t, serverId, streamId, now - t.Since, now));
            t.DownAlerted = true;
        }
    }

    private AlertEvent Build(AlertKind kind, Tracked t, int serverId, int? streamId, TimeSpan downtime, DateTime at)
    {
        using var _ = UseDefaultLanguage();
        var time = Format.Duration(downtime);
        var (title, message) = kind switch
        {
            AlertKind.ServerDown => (L["Server {0} is offline", t.Name],
                L["The panel cannot reach MediaMTX server {0} (for {1}). Streams on this server are probably unavailable.", t.Name, time]),
            AlertKind.ServerUp => (L["Server {0} is back online", t.Name],
                L["MediaMTX server {0} can be reached again after {1}.", t.Name, time]),
            AlertKind.StreamDown => (L["Stream {0} is offline", t.Name],
                L["Stream {0} on server {1} stopped (for {2}). The publisher or source is no longer sending.", t.Name, t.ServerName ?? "", time]),
            _ => (L["Stream {0} is live again", t.Name],
                L["Stream {0} on server {1} is live again after {2}.", t.Name, t.ServerName ?? "", time]),
        };
        return new AlertEvent(kind, title, message, serverId, t.ServerName ?? t.Name, streamId,
            kind is AlertKind.StreamDown or AlertKind.StreamUp ? t.Name : null, downtime, at);
    }

    /// <summary>Sends a test alert through the given settings (also before saving); returns the log entry.</summary>
    public async Task<AlertLogEntry> SendTestAsync(AlertSettings settings, CancellationToken ct = default)
    {
        AlertEvent e;
        using (UseDefaultLanguage())
            e = new AlertEvent(AlertKind.Test, L["Test alert from {0}", store.Current.AppName],
                L["This is a test alert. When a server or stream goes offline, you get a message like this."],
                null, null, null, null, null, DateTime.UtcNow);
        return await SendAsync(e, settings, ct);
    }

    private async Task<AlertLogEntry> SendAsync(AlertEvent e, AlertSettings settings, CancellationToken ct)
    {
        var errors = new List<string>();
        bool mailSent = false, webhookSent = false;

        var recipients = settings.EmailRecipients.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (recipients.Count > 0)
        {
            if (!mailer.IsConfigured)
                errors.Add("Mail: no mail server configured under Settings → Mail");
            else
            {
                var html = MailHtml(e);
                foreach (var to in recipients)
                {
                    try
                    {
                        await mailer.SendAsync(to, e.Title, html, ct: ct);
                        mailSent = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        errors.Add($"Mail to {to}: {ex.Message}");
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.WebhookUrl))
        {
            try
            {
                await PostWebhookAsync(e, settings, ct);
                webhookSent = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add($"Webhook: {ex.Message}");
            }
        }

        var entry = new AlertLogEntry(e, mailSent, webhookSent, errors.Count > 0 ? string.Join("; ", errors) : null);
        if (e.Kind != AlertKind.Test)
        {
            history.Enqueue(entry);
            while (history.Count > HistorySize && history.TryDequeue(out _)) { }
            logger.LogWarning("Alert {Event}: {Title}", e.EventName, e.Title);
        }
        if (entry.Error is not null)
            logger.LogError("Alert {Event} could not be delivered: {Error}", e.EventName, entry.Error);
        Changed?.Invoke();
        return entry;
    }

    private string MailHtml(AlertEvent e)
    {
        var color = e.Kind switch
        {
            AlertKind.ServerDown or AlertKind.StreamDown => "#dc3545",
            AlertKind.ServerUp or AlertKind.StreamUp => "#198754",
            _ => "#0d6efd",
        };
        string at;
        using (UseDefaultLanguage())
            at = L["Time: {0} (UTC)", e.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)];
        return $"""
            <div style="font-family:system-ui,-apple-system,Segoe UI,sans-serif;max-width:560px">
              <div style="border-left:4px solid {color};padding:12px 16px;background:#f8f9fa">
                <h2 style="margin:0 0 8px;font-size:18px;color:{color}">{WebUtility.HtmlEncode(e.Title)}</h2>
                <p style="margin:0 0 8px">{WebUtility.HtmlEncode(e.Message)}</p>
                <p style="margin:0;color:#6c757d;font-size:13px">{WebUtility.HtmlEncode(at)} · {WebUtility.HtmlEncode(store.Current.AppName)}</p>
              </div>
            </div>
            """;
    }

    private async Task PostWebhookAsync(AlertEvent e, AlertSettings settings, CancellationToken ct)
    {
        var body = WebhookBody(e, settings.WebhookFormat).ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.WebhookUrl!.Trim())
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Panel-Event", e.EventName);
        if (Unprotect(settings.WebhookSecretProtected) is { Length: > 0 } secret)
        {
            var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
            request.Headers.Add("X-Panel-Signature", "sha256=" + Convert.ToHexStringLower(hash));
        }
        using var response = await httpClientFactory.CreateClient("alerts").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private JsonNode WebhookBody(AlertEvent e, WebhookFormat format)
    {
        var icon = e.Kind switch
        {
            AlertKind.ServerDown or AlertKind.StreamDown => "🔴",
            AlertKind.ServerUp or AlertKind.StreamUp => "🟢",
            _ => "🔔",
        };
        var text = $"{icon} {e.Title}\n{e.Message}";
        return format switch
        {
            WebhookFormat.Slack => new JsonObject { ["text"] = text },
            WebhookFormat.Discord => new JsonObject { ["content"] = text },
            WebhookFormat.Teams => new JsonObject
            {
                ["type"] = "message",
                ["attachments"] = new JsonArray(new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["content"] = new JsonObject
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = "TextBlock", ["text"] = $"{icon} {e.Title}", ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true,
                                ["color"] = e.IsProblem ? "Attention" : e.Kind == AlertKind.Test ? "Accent" : "Good",
                            },
                            new JsonObject { ["type"] = "TextBlock", ["text"] = e.Message, ["wrap"] = true },
                            new JsonObject { ["type"] = "TextBlock", ["text"] = store.Current.AppName, ["isSubtle"] = true, ["size"] = "Small" }),
                    },
                }),
            },
            _ => new JsonObject
            {
                ["event"] = e.EventName,
                ["problem"] = e.IsProblem,
                ["title"] = e.Title,
                ["message"] = e.Message,
                ["server"] = e.ServerId is null ? null : new JsonObject { ["id"] = e.ServerId, ["name"] = e.ServerName },
                ["stream"] = e.StreamId is null ? null : new JsonObject { ["id"] = e.StreamId, ["name"] = e.StreamName },
                ["downtimeSeconds"] = e.Downtime is { } d ? (int)d.TotalSeconds : null,
                ["at"] = e.At.ToString("O", CultureInfo.InvariantCulture),
                ["panel"] = store.Current.AppName,
            },
        };
    }

    private string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return Protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            logger.LogWarning("The stored webhook secret cannot be decrypted (data protection keys changed); enter it again under Settings → Alerts.");
            return null;
        }
    }

    /// <summary>Alerts are written in the panel's default language, whoever triggered them.</summary>
    private IDisposable UseDefaultLanguage()
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var language = new CultureInfo(store.Current.DefaultLanguage);
        (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (language, language);
        return new Restore(() => (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture));
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
