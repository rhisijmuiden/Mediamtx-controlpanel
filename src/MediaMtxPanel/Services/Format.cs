using System.Globalization;

namespace MediaMtxPanel.Services;

public static class Format
{
    private static readonly CultureInfo Nl = CultureInfo.GetCultureInfo("nl-NL");

    public static string Bytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return $"{v:0.#} {units[i]}";
    }

    /// <summary>Bytes per second as bits per second, e.g. "4.2 Mbit/s".</summary>
    public static string Bitrate(double? bytesPerSecond)
    {
        if (bytesPerSecond is null)
            return "–";
        var bits = bytesPerSecond.Value * 8;
        string[] units = ["bit/s", "kbit/s", "Mbit/s", "Gbit/s"];
        var i = 0;
        while (bits >= 1000 && i < units.Length - 1)
        {
            bits /= 1000;
            i++;
        }
        return $"{bits:0.#} {units[i]}";
    }

    public static string Money(int cents, string currency = "EUR") =>
        (cents / 100m).ToString("C", currency == "EUR" ? Nl : CultureInfo.InvariantCulture)
        + (currency == "EUR" ? "" : $" {currency}");

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}u" :
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}u {t.Minutes}m" :
        $"{t.Minutes}m {t.Seconds}s";

    public static string Hours(int hours) =>
        hours % 24 == 0 ? (hours == 24 ? "1 dag" : $"{hours / 24} dagen") : $"{hours} uur";
}
