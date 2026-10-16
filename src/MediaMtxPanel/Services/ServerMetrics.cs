using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using MediaMtxPanel.Data;

namespace MediaMtxPanel.Services;

/// <summary>Host statistics of a MediaMTX server.</summary>
public record HostMetrics(
    double? CpuPercent,
    int? CpuCores,
    double? Load1,
    ulong? MemTotalBytes,
    ulong? MemUsedBytes,
    double? NetRxBytesPerSec,
    double? NetTxBytesPerSec,
    TimeSpan? Uptime,
    string Source)
{
    public double? MemPercent => MemTotalBytes is > 0 && MemUsedBytes is { } used ? 100.0 * used / MemTotalBytes.Value : null;
}

/// <summary>
/// Reads CPU, memory and network usage of a server. Rates (CPU %, network) need two samples, so the
/// previous raw sample per server is kept and the first call after start returns no rate yet.
/// </summary>
public class ServerMetricsReader(IHttpClientFactory httpClientFactory, ILogger<ServerMetricsReader> logger)
{
    private record RawSample(DateTime At, double CpuBusy, double CpuTotal, double NetRx, double NetTx);

    private readonly ConcurrentDictionary<int, RawSample> _previous = new();
    private readonly ConcurrentDictionary<int, (DateTime At, HostMetrics Metrics)> _cache = new();

    /// <summary>Whether host statistics can be read for this server at all.</summary>
    public static bool IsSupported(MediaServer server) =>
        !string.IsNullOrWhiteSpace(server.MetricsUrl) || (IsLocal(server) && File.Exists("/proc/stat"));

    public async Task<HostMetrics?> GetAsync(MediaServer server, CancellationToken ct = default)
    {
        // Many viewers poll at once; one sample per server every two seconds is plenty.
        if (_cache.TryGetValue(server.Id, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(2))
            return cached.Metrics;

        HostMetrics? metrics = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(server.MetricsUrl))
                metrics = await ReadNodeExporterAsync(server, ct);
            else if (IsLocal(server) && File.Exists("/proc/stat"))
                metrics = ReadLocalProc(server);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or FormatException)
        {
            logger.LogDebug("Could not read metrics of {Server}: {Message}", server.Name, ex.Message);
        }

        if (metrics is not null)
            _cache[server.Id] = (DateTime.UtcNow, metrics);
        return metrics;
    }

    private static bool IsLocal(MediaServer server)
    {
        if (!Uri.TryCreate(server.ApiUrl, UriKind.Absolute, out var uri))
            return false;
        return uri.IsLoopback || (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip));
    }

    // ------------------------------------------------------------------ local /proc

    private HostMetrics ReadLocalProc(MediaServer server)
    {
        var cpu = File.ReadLines("/proc/stat").First(l => l.StartsWith("cpu "))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(double.Parse).ToArray();
        // user nice system idle iowait irq softirq steal
        var idle = cpu[3] + (cpu.Length > 4 ? cpu[4] : 0);
        var total = cpu.Take(Math.Min(cpu.Length, 8)).Sum();
        var cores = File.ReadLines("/proc/stat").Count(l => l.StartsWith("cpu") && l.Length > 3 && char.IsDigit(l[3]));

        var mem = File.ReadLines("/proc/meminfo")
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => ulong.Parse(p[1].Trim().Split(' ')[0]) * 1024);
        var memTotal = mem.GetValueOrDefault("MemTotal");
        var memUsed = memTotal - mem.GetValueOrDefault("MemAvailable", mem.GetValueOrDefault("MemFree"));

        double rx = 0, tx = 0;
        foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2 || parts[0].Trim() == "lo")
                continue;
            var f = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rx += double.Parse(f[0], CultureInfo.InvariantCulture);
            tx += double.Parse(f[8], CultureInfo.InvariantCulture);
        }

        double? load = null;
        if (File.Exists("/proc/loadavg"))
            load = double.Parse(File.ReadAllText("/proc/loadavg").Split(' ')[0], CultureInfo.InvariantCulture);
        TimeSpan? uptime = null;
        if (File.Exists("/proc/uptime"))
            uptime = TimeSpan.FromSeconds(double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture));

        var sample = new RawSample(DateTime.UtcNow, total - idle, total, rx, tx);
        var (cpuPct, rxRate, txRate) = Rates(server.Id, sample);
        return new HostMetrics(cpuPct, cores, load, memTotal, memUsed, rxRate, txRate, uptime, "local");
    }

    // ------------------------------------------------------------------ node_exporter

    private async Task<HostMetrics> ReadNodeExporterAsync(MediaServer server, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("metrics");
        http.Timeout = TimeSpan.FromSeconds(5);
        var text = await http.GetStringAsync(server.MetricsUrl, ct);

        double cpuIdle = 0, cpuTotal = 0, rx = 0, tx = 0;
        double? memTotal = null, memAvail = null, load = null, boot = null, now = null;
        var cpus = new HashSet<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var space = line.LastIndexOf(' ');
            if (space < 0 || !double.TryParse(line[(space + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;
            var key = line[..space];

            if (key.StartsWith("node_cpu_seconds_total{"))
            {
                cpuTotal += value;
                if (key.Contains("mode=\"idle\"") || key.Contains("mode=\"iowait\""))
                    cpuIdle += value;
                var cpu = Label(key, "cpu");
                if (cpu is not null) cpus.Add(cpu);
            }
            else if (key == "node_memory_MemTotal_bytes") memTotal = value;
            else if (key == "node_memory_MemAvailable_bytes") memAvail = value;
            else if (key == "node_load1") load = value;
            else if (key == "node_boot_time_seconds") boot = value;
            else if (key == "node_time_seconds") now = value;
            else if (key.StartsWith("node_network_receive_bytes_total{") && !IsVirtualDevice(key)) rx += value;
            else if (key.StartsWith("node_network_transmit_bytes_total{") && !IsVirtualDevice(key)) tx += value;
        }

        var sample = new RawSample(DateTime.UtcNow, cpuTotal - cpuIdle, cpuTotal, rx, tx);
        var (cpuPct, rxRate, txRate) = Rates(server.Id, sample);
        return new HostMetrics(
            cpuPct,
            cpus.Count > 0 ? cpus.Count : null,
            load,
            memTotal is null ? null : (ulong)memTotal.Value,
            memTotal is null || memAvail is null ? null : (ulong)(memTotal.Value - memAvail.Value),
            rxRate, txRate,
            boot is not null && now is not null ? TimeSpan.FromSeconds(now.Value - boot.Value) : null,
            "node_exporter");
    }

    private static string? Label(string key, string name)
    {
        var start = key.IndexOf(name + "=\"", StringComparison.Ordinal);
        if (start < 0) return null;
        start += name.Length + 2;
        var end = key.IndexOf('"', start);
        return end < 0 ? null : key[start..end];
    }

    private static bool IsVirtualDevice(string key)
    {
        var dev = Label(key, "device") ?? "";
        return dev == "lo" || dev.StartsWith("veth") || dev.StartsWith("docker") || dev.StartsWith("br-");
    }

    private (double? Cpu, double? Rx, double? Tx) Rates(int serverId, RawSample sample)
    {
        double? cpu = null, rxRate = null, txRate = null;
        if (_previous.TryGetValue(serverId, out var prev))
        {
            var dt = (sample.At - prev.At).TotalSeconds;
            var dTotal = sample.CpuTotal - prev.CpuTotal;
            if (dTotal > 0)
                cpu = Math.Clamp(100.0 * (sample.CpuBusy - prev.CpuBusy) / dTotal, 0, 100);
            if (dt > 0 && sample.NetRx >= prev.NetRx && sample.NetTx >= prev.NetTx)
            {
                rxRate = (sample.NetRx - prev.NetRx) / dt;
                txRate = (sample.NetTx - prev.NetTx) / dt;
            }
        }
        _previous[serverId] = sample;
        return (cpu, rxRate, txRate);
    }
}
