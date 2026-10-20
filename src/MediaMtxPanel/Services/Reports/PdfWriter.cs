using System.Globalization;
using System.Text;

namespace MediaMtxPanel.Services.Reports;

/// <summary>
/// A very small PDF writer for the panel's reports: A4 pages with text in the standard Helvetica fonts,
/// filled rectangles and lines. The standard fonts are built into every PDF reader, so nothing has to be
/// embedded and no PDF library (or its license) is needed. Text is limited to the Windows-1252 characters
/// that Helvetica has; anything else prints as "?".
/// </summary>
public sealed class PdfWriter
{
    public const double PageWidth = 595.28;
    public const double PageHeight = 841.89;

    private readonly List<StringBuilder> _pages = [];
    private StringBuilder? _page;

    public int PageCount => _pages.Count;

    public void NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
    }

    /// <summary>Switches to an existing page (0-based), e.g. to add page numbers at the end.</summary>
    public void UsePage(int index) => _page = _pages[index];

    private StringBuilder Page => _page ?? throw new InvalidOperationException("Call NewPage first.");

    /// <summary>Text with its baseline at <paramref name="y"/>, measured from the top of the page.</summary>
    public void Text(double x, double y, string text, double size, bool bold = false, string color = "#111827", TextAlign align = TextAlign.Left)
    {
        var width = Measure(text, size, bold);
        if (align == TextAlign.Right) x -= width;
        else if (align == TextAlign.Center) x -= width / 2;
        Page.Append(CultureInfo.InvariantCulture,
            $"BT {Rgb(color)} rg /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(PageHeight - y)} Td ({Escape(text)}) Tj ET\n");
    }

    /// <summary>Filled rectangle; <paramref name="y"/> is its top edge, measured from the top of the page.</summary>
    public void Rect(double x, double y, double w, double h, string fill) =>
        Page.Append(CultureInfo.InvariantCulture, $"{Rgb(fill)} rg {N(x)} {N(PageHeight - y - h)} {N(w)} {N(h)} re f\n");

    public void Line(double x1, double y1, double x2, double y2, string color, double width = 0.5, bool dashed = false) =>
        Page.Append(CultureInfo.InvariantCulture,
            $"{(dashed ? "[2 2] 0 d " : "")}{Rgb(color)} RG {N(width)} w {N(x1)} {N(PageHeight - y1)} m {N(x2)} {N(PageHeight - y2)} l S{(dashed ? " [] 0 d" : "")}\n");

    /// <summary>Width of <paramref name="text"/> in points.</summary>
    public static double Measure(string text, double size, bool bold = false)
    {
        var widths = bold ? HelveticaBold : Helvetica;
        double units = 0;
        foreach (var b in Encode(text))
            units += b is >= 32 and <= 126 ? widths[b - 32] : 556;
        return units * size / 1000;
    }

    /// <summary><paramref name="text"/>, shortened with "…" so that it fits in <paramref name="width"/>.</summary>
    public static string Fit(string text, double width, double size, bool bold = false)
    {
        if (Measure(text, size, bold) <= width)
            return text;
        while (text.Length > 1 && Measure(text + "…", size, bold) > width)
            text = text[..^1];
        return text.TrimEnd() + "…";
    }

    public byte[] ToArray()
    {
        var objects = new List<byte[]>();
        int Add(string content)
        {
            objects.Add(Encoding.Latin1.GetBytes(content));
            return objects.Count;
        }

        // Fixed numbering: 1 catalog, 2 pages, 3-4 fonts, then a content stream and a page object per page.
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, _pages.Count).Select(i => $"{6 + i * 2} 0 R"));
        Add($"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        foreach (var page in _pages)
        {
            var content = page.ToString();
            var stream = Add($"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream");
            Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {stream} 0 R >>");
        }

        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        Write("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            Write($"{i + 1} 0 obj\n");
            ms.Write(objects[i]);
            Write("\nendobj\n");
        }
        var xref = ms.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    // Characters of Windows-1252 that differ from Latin-1 and are likely in names and texts.
    private static readonly Dictionary<char, byte> Cp1252 = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['„'] = 0x84, ['…'] = 0x85, ['‘'] = 0x91, ['’'] = 0x92,
        ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['™'] = 0x99,
        ['Š'] = 0x8A, ['š'] = 0x9A, ['Œ'] = 0x8C, ['œ'] = 0x9C, ['Ž'] = 0x8E, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    private static IEnumerable<byte> Encode(string text)
    {
        foreach (var c in text)
        {
            if (Cp1252.TryGetValue(c, out var b)) yield return b;
            else if (c is >= ' ' and <= '~' or >= ' ' and <= 'ÿ') yield return (byte)c;
            else if (c == ' ' || c == ' ') yield return 0xA0; // narrow spaces some cultures use in numbers
            else yield return (byte)'?';
        }
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder();
        foreach (var b in Encode(text))
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\').Append((char)b);
            else sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static string Rgb(string hex)
    {
        var v = Convert.ToInt32(hex.TrimStart('#'), 16);
        return $"{N(((v >> 16) & 255) / 255.0)} {N(((v >> 8) & 255) / 255.0)} {N((v & 255) / 255.0)}";
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // Advance widths of the printable ASCII characters (32-126) from the Adobe font metrics.
    private static readonly short[] Helvetica =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly short[] HelveticaBold =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];
}

public enum TextAlign
{
    Left,
    Center,
    Right,
}
