using System.Globalization;
using MediaMtxPanel.Components.Shared;
using MediaMtxPanel.Services.Localization;

namespace MediaMtxPanel.Services.Reports;

/// <summary>The viewer report as a printable A4 PDF: key figures, a chart and the tables of the Reports page.</summary>
public static class ViewerReportPdf
{
    private const double Margin = 40;
    private const double Width = PdfWriter.PageWidth - 2 * Margin;
    private const double Bottom = PdfWriter.PageHeight - Margin - 24;
    private const string Muted = "#6b7280";
    private const string Border = "#e5e7eb";
    private const string Primary = "#4f46e5";
    private const string Secondary = "#0ea5e9";

    private record Column(string Title, double Width, bool Right);

    public static byte[] Render(ViewerReport report, string appName, TextLocalizer l, DateTime generatedAt)
    {
        var pdf = new PdfWriter();
        pdf.NewPage();
        double y = Margin;

        // Title
        pdf.Text(Margin, y + 9, appName, 9, color: Muted);
        pdf.Text(Margin + Width, y + 9, l["Generated {0}", generatedAt.ToString("g", CultureInfo.CurrentCulture)], 8, color: Muted, align: TextAlign.Right);
        pdf.Text(Margin, y + 34, l["Viewer report"], 22, bold: true);
        var scope = report.StreamId is null ? l["All streams"] : report.StreamName;
        pdf.Text(Margin, y + 52, $"{scope} · {report.From.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} – {report.To.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} · {GroupingLabel(report.Grouping, l)}", 10, color: Muted);
        y += 66;
        pdf.Rect(Margin, y, Width, 2, Primary);
        y += 16;

        // Key figures, three per row
        var t = report.Totals;
        (string Label, string Value)[] figures =
        [
            (l["Peak viewers"], Count(t.PeakViewers)),
            (l["Unique viewers"], Count(t.UniqueViewers)),
            (l["Views"], Count(t.Sessions)),
            (l["Watch time"], Hours(t.WatchHours)),
            (l["Average view duration"], t.Sessions > 0 ? Format.Duration(t.AvgViewDuration) : "–"),
            (l["Data sent to viewers"], Format.Bytes((ulong)t.BytesOut)),
        ];
        const double gap = 8, boxH = 46;
        var boxW = (Width - 2 * gap) / 3;
        for (var i = 0; i < figures.Length; i++)
        {
            var x = Margin + i % 3 * (boxW + gap);
            var top = y + i / 3 * (boxH + gap);
            pdf.Rect(x, top, boxW, boxH, "#f5f6fa");
            pdf.Rect(x, top, 3, boxH, i % 2 == 0 ? Primary : Secondary);
            pdf.Text(x + 12, top + 16, figures[i].Label, 8, color: Muted);
            pdf.Text(x + 12, top + 36, figures[i].Value, 15, bold: true);
        }
        y += 2 * boxH + gap + 22;

        if (report.IsEmpty)
        {
            pdf.Text(Margin, y, l["No viewers were recorded in this period."], 10, color: Muted);
            y += 24;
        }

        // Chart
        if (report.Periods.Count > 1)
        {
            y = Chart(pdf, report, l, y);
        }

        // Tables
        var countW = 52.0;
        var periodColumns = new List<Column>
        {
            new(l["Period"], Width - 4 * countW - 3 * 70, false),
            new(l["Peak"], countW, true), new(l["Unique"], countW, true), new(l["Views"], countW, true),
            new(l["Watch time"], 70, true), new(l["Avg. per view"], 70, true), new(l["Data sent"], countW + 18, true),
        };
        periodColumns[0] = periodColumns[0] with { Width = Width - periodColumns.Skip(1).Sum(c => c.Width) };
        y = Table(pdf, l["Per period"], periodColumns, report.Periods.Select(p =>
            Cells(ViewerReportService.PeriodLabel(p, report.Grouping, l), p.Totals)).ToList(),
            Cells(l["Total"], report.Totals), y);

        if (report.StreamId is null && report.Streams.Count > 0)
        {
            var streamColumns = periodColumns.ToList();
            streamColumns[0] = streamColumns[0] with { Title = l["Stream"] };
            y = Table(pdf, l["Per stream"], streamColumns, report.Streams.Select(s =>
                Cells(s.Exists ? s.Name : $"{s.Name} ({l["deleted"]})", s.Totals)).ToList(), null, y + 12);
        }

        // Footer on every page
        for (var i = 0; i < pdf.PageCount; i++)
        {
            pdf.UsePage(i);
            var fy = PdfWriter.PageHeight - Margin + 6;
            pdf.Line(Margin, fy - 12, Margin + Width, fy - 12, Border);
            pdf.Text(Margin, fy, $"{appName} · {l["Viewer report"]}", 7.5, color: Muted);
            pdf.Text(Margin + Width, fy, l["Page {0} of {1}", i + 1, pdf.PageCount], 7.5, color: Muted, align: TextAlign.Right);
        }
        return pdf.ToArray();
    }

    private static double Chart(PdfWriter pdf, ViewerReport report, TextLocalizer l, double y)
    {
        const double height = 150, axisW = 34;
        if (y + height + 60 > Bottom)
        {
            pdf.NewPage();
            y = Margin;
        }
        pdf.Text(Margin, y, l["Viewers"], 11, bold: true);
        // Legend
        var lx = Margin + Width;
        foreach (var (label, color) in new[] { (l["Unique viewers"], Secondary), (l["Peak viewers"], Primary) })
        {
            lx -= PdfWriter.Measure(label, 8) + 4;
            pdf.Text(lx, y, label, 8, color: Muted);
            lx -= 10;
            pdf.Rect(lx, y - 6.5, 7, 7, color);
            lx -= 12;
        }
        y += 12;

        var periods = report.Periods;
        var max = TimeChart.NiceCeiling(periods.Max(p => Math.Max(p.Totals.PeakViewers, p.Totals.UniqueViewers)));
        var plotX = Margin + axisW;
        var plotW = Width - axisW;
        for (var i = 0; i <= 4; i++)
        {
            var gy = y + height - height * i / 4;
            pdf.Line(plotX, gy, plotX + plotW, gy, Border, dashed: i > 0);
            pdf.Text(plotX - 6, gy + 3, (max * i / 4).ToString("0.#", CultureInfo.CurrentCulture), 7, color: Muted, align: TextAlign.Right);
        }

        var slot = plotW / periods.Count;
        var bar = Math.Max(0.6, Math.Min(14, slot * 0.36));
        var labelEvery = Math.Max(1, (int)Math.Ceiling(periods.Count / (plotW / 46)));
        for (var i = 0; i < periods.Count; i++)
        {
            var cx = plotX + slot * (i + 0.5);
            var peak = height * periods[i].Totals.PeakViewers / max;
            var unique = height * periods[i].Totals.UniqueViewers / max;
            if (peak > 0) pdf.Rect(cx - bar, y + height - peak, bar, peak, Primary);
            if (unique > 0) pdf.Rect(cx, y + height - unique, bar, unique, Secondary);
            if (i % labelEvery == 0)
                pdf.Text(cx, y + height + 11, ShortLabel(periods[i], report.Grouping), 7, color: Muted, align: TextAlign.Center);
        }
        return y + height + 34;
    }

    private static double Table(PdfWriter pdf, string title, List<Column> columns, List<string[]> rows, string[]? total, double y)
    {
        const double rowH = 16, size = 8.5;
        void Header()
        {
            pdf.Rect(Margin, y, Width, rowH, "#eef0f6");
            var x = Margin;
            foreach (var c in columns)
            {
                pdf.Text(c.Right ? x + c.Width - 6 : x + 6, y + 11, PdfWriter.Fit(c.Title, c.Width - 8, 8, true), 8, bold: true, color: "#374151",
                    align: c.Right ? TextAlign.Right : TextAlign.Left);
                x += c.Width;
            }
            y += rowH;
        }
        void Row(string[] cells, bool bold, string? fill)
        {
            if (y + rowH > Bottom)
            {
                pdf.NewPage();
                y = Margin;
                Header();
            }
            if (fill is not null) pdf.Rect(Margin, y, Width, rowH, fill);
            var x = Margin;
            for (var i = 0; i < columns.Count; i++)
            {
                var c = columns[i];
                pdf.Text(c.Right ? x + c.Width - 6 : x + 6, y + 11, PdfWriter.Fit(cells[i], c.Width - 10, size, bold), size, bold: bold,
                    align: c.Right ? TextAlign.Right : TextAlign.Left);
                x += c.Width;
            }
            y += rowH;
        }

        if (y + 3 * rowH + 18 > Bottom)
        {
            pdf.NewPage();
            y = Margin;
        }
        pdf.Text(Margin, y + 4, title, 11, bold: true);
        y += 12;
        Header();
        for (var i = 0; i < rows.Count; i++)
            Row(rows[i], false, i % 2 == 1 ? "#fafafb" : null);
        if (total is not null)
        {
            pdf.Line(Margin, y, Margin + Width, y, "#9ca3af");
            Row(total, true, null);
        }
        return y + 10;
    }

    private static string[] Cells(string label, ViewerTotals t) =>
    [
        label, Count(t.PeakViewers), Count(t.UniqueViewers), Count(t.Sessions), Hours(t.WatchHours),
        t.Sessions > 0 ? Format.Duration(t.AvgViewDuration) : "–", Format.Bytes((ulong)t.BytesOut),
    ];

    private static string Count(int v) => v.ToString("N0", CultureInfo.CurrentCulture);

    private static string Hours(double h) => h.ToString(h >= 100 ? "N0" : "0.#", CultureInfo.CurrentCulture) + " h";

    internal static string GroupingLabel(ReportGrouping g, TextLocalizer l) => g switch
    {
        ReportGrouping.Week => l["per week"],
        ReportGrouping.Month => l["per month"],
        _ => l["per day"],
    };

    private static string ShortLabel(ReportPeriod p, ReportGrouping g) => g switch
    {
        ReportGrouping.Week => "W" + ISOWeek.GetWeekOfYear(p.Start.ToDateTime(TimeOnly.MinValue)),
        ReportGrouping.Month => p.Start.ToString("MMM yy", CultureInfo.CurrentCulture),
        _ => p.Start.ToString("d MMM", CultureInfo.CurrentCulture),
    };
}
