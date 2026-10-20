using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MediaMtxPanel.Services;

/// <summary>One version in CHANGELOG.md, newest first. <see cref="Version"/> is null for the Unreleased section.</summary>
public sealed record ChangelogRelease(string? Version, DateOnly? Date, IReadOnlyList<ChangelogSection> Sections)
{
    public bool Unreleased => Version is null;
    public bool IsEmpty => Sections.All(s => s.Items.Count == 0);
}

/// <summary>A group of changes such as Added or Fixed.</summary>
public sealed record ChangelogSection(string Title, IReadOnlyList<string> Items);

/// <summary>
/// Reads CHANGELOG.md in the Keep a Changelog format: "## [1.2.0] - 2026-10-18", "### Added" and "- item" lines.
/// The file is embedded in the app (Info → Changelog) and fetched from GitHub to show what a newer version brings.
/// </summary>
public static partial class Changelog
{
    private static readonly Lazy<IReadOnlyList<ChangelogRelease>> bundled = new(() =>
    {
        using var stream = typeof(Changelog).Assembly.GetManifestResourceStream("CHANGELOG.md");
        return stream is null ? [] : Parse(new StreamReader(stream).ReadToEnd());
    });

    /// <summary>The changelog this build was made with.</summary>
    public static IReadOnlyList<ChangelogRelease> Bundled => bundled.Value;

    public static IReadOnlyList<ChangelogRelease> Parse(string markdown)
    {
        var releases = new List<ChangelogRelease>();
        string? version = null;
        DateOnly? date = null;
        List<ChangelogSection>? sections = null;
        string? sectionTitle = null;
        List<string>? items = null;
        var inRelease = false;

        void CloseSection()
        {
            if (sectionTitle is not null && items is { Count: > 0 })
                sections!.Add(new ChangelogSection(sectionTitle, items));
            sectionTitle = null;
            items = null;
        }

        void CloseRelease()
        {
            CloseSection();
            if (inRelease)
                releases.Add(new ChangelogRelease(version, date, sections!));
            inRelease = false;
        }

        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (ReleaseHeading().Match(line) is { Success: true } heading)
            {
                CloseRelease();
                var name = heading.Groups["name"].Value.Trim();
                inRelease = true;
                version = name.Equals("Unreleased", StringComparison.OrdinalIgnoreCase) ? null : name.TrimStart('v', 'V');
                date = DateOnly.TryParseExact(heading.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d) ? d : null;
                sections = [];
            }
            else if (!inRelease)
            {
                continue;
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                CloseSection();
                sectionTitle = line[4..].Trim();
                items = [];
            }
            else if (items is not null && BulletItem().Match(line) is { Success: true } bullet)
            {
                items.Add(bullet.Groups["text"].Value.Trim());
            }
            else if (items is { Count: > 0 } && line.Length > 0 && char.IsWhiteSpace(raw[0]))
            {
                // Continuation line of a wrapped bullet.
                items[^1] += " " + line.Trim();
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal) || LinkReference().IsMatch(line))
            {
                CloseRelease();
            }
        }
        CloseRelease();
        return releases;
    }

    /// <summary>
    /// The releases in <paramref name="changelog"/> that are newer than <paramref name="current"/>, newest first.
    /// <paramref name="includeUnreleased"/> adds the Unreleased section (for installs that follow the main branch).
    /// </summary>
    public static IReadOnlyList<ChangelogRelease> NewerThan(IEnumerable<ChangelogRelease> changelog, string current, bool includeUnreleased) =>
        changelog.Where(r => !r.IsEmpty && (r.Unreleased ? includeUnreleased : ReleaseVersion.IsNewer(r.Version!, current))).ToList();

    /// <summary>
    /// Turns one changelog item into safe HTML: the text is encoded first, then **bold**, `code`,
    /// [links](https://…) and pull request numbers (#12) are formatted.
    /// </summary>
    public static string ToHtml(string item, string? repository)
    {
        var html = WebUtility.HtmlEncode(item);
        html = InlineCode().Replace(html, "<code>$1</code>");
        html = Bold().Replace(html, "<strong>$1</strong>");
        html = Link().Replace(html, m =>
        {
            var url = WebUtility.HtmlDecode(m.Groups["url"].Value);
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
                ? $"<a href=\"{WebUtility.HtmlEncode(uri.ToString())}\" target=\"_blank\" rel=\"noopener\">{m.Groups["text"].Value}</a>"
                : m.Groups["text"].Value;
        });
        if (!string.IsNullOrWhiteSpace(repository))
        {
            var repo = WebUtility.HtmlEncode(repository.Trim('/'));
            html = PullRequest().Replace(html, m =>
                $"<a class=\"changelog-pr\" href=\"https://github.com/{repo}/pull/{m.Groups[1].Value}\" target=\"_blank\" rel=\"noopener\">#{m.Groups[1].Value}</a>");
        }
        return html;
    }

    [GeneratedRegex(@"^##\s+\[?(?<name>[^\]\s]+)\]?(?:\s+[-–]\s+(?<date>\d{4}-\d{2}-\d{2}))?")]
    private static partial Regex ReleaseHeading();

    [GeneratedRegex(@"^\s{0,3}[-*]\s+(?<text>.+)$")]
    private static partial Regex BulletItem();

    [GeneratedRegex(@"^\[[^\]]+\]:\s*\S+")]
    private static partial Regex LinkReference();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)\)")]
    private static partial Regex Link();

    // #12 at the start, after a space or an opening bracket; not inside a URL or an HTML entity such as &#39;.
    [GeneratedRegex(@"(?<=^|[\s(])#(\d+)\b")]
    private static partial Regex PullRequest();
}
