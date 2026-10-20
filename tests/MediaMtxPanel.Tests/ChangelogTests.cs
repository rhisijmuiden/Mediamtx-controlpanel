using MediaMtxPanel.Services;

namespace MediaMtxPanel.Tests;

public class ChangelogTests
{
    private const string Sample = """
        # Changelog

        Some text with a - dash.

        ## [Unreleased]

        ### Added

        - Something new on main.

        ## [1.3.0] - 2026-10-20

        ### Added

        - **Backups** over SFTP. (#18)
        - A long item
          that wraps.

        ### Fixed

        - A bug.

        ## [1.2.0] - 2026-10-18

        ### Changed

        - Version compare. (#12)

        [Unreleased]: https://github.com/o/r/compare/v1.3.0...HEAD
        [1.3.0]: https://github.com/o/r/releases/tag/v1.3.0
        """;

    [Fact]
    public void Parses_versions_dates_sections_and_items()
    {
        var releases = Changelog.Parse(Sample);

        Assert.Equal([null, "1.3.0", "1.2.0"], releases.Select(r => r.Version));
        Assert.True(releases[0].Unreleased);
        Assert.Equal(new DateOnly(2026, 10, 20), releases[1].Date);
        Assert.Equal(["Added", "Fixed"], releases[1].Sections.Select(s => s.Title));
        Assert.Equal(["**Backups** over SFTP. (#18)", "A long item that wraps."], releases[1].Sections[0].Items);
        Assert.Single(releases[2].Sections[0].Items);
    }

    [Fact]
    public void Newer_than_skips_installed_versions_and_only_includes_unreleased_when_asked()
    {
        var releases = Changelog.Parse(Sample);

        Assert.Equal(["1.3.0"], Changelog.NewerThan(releases, "1.2.0", includeUnreleased: false).Select(r => r.Version));
        Assert.Equal([null, "1.3.0"], Changelog.NewerThan(releases, "1.2.0", includeUnreleased: true).Select(r => r.Version));
        Assert.Empty(Changelog.NewerThan(releases, "1.3.0", includeUnreleased: false));
        Assert.Equal(["1.3.0"], Changelog.NewerThan(releases, "1.3.0-test.2", includeUnreleased: false).Select(r => r.Version));
    }

    [Fact]
    public void Html_is_encoded_and_formats_bold_code_links_and_pull_requests()
    {
        var html = Changelog.ToHtml("**New** `cmd` <script>x</script> [docs](https://example.com/a) [bad](javascript:alert(1)) it's (#12)", "o/r");

        Assert.Contains("<strong>New</strong>", html);
        Assert.Contains("<code>cmd</code>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("<a href=\"https://example.com/a\"", html);
        Assert.DoesNotContain("javascript:", html.Replace("[bad](javascript:", ""));
        Assert.Contains("href=\"https://github.com/o/r/pull/12\"", html);
        Assert.DoesNotContain("/pull/39", html); // the apostrophe becomes &#39;
    }

    [Fact]
    public void Bundled_changelog_has_a_section_for_the_current_version()
    {
        var current = BuildInfo.Version;
        Assert.Contains(Changelog.Bundled, r => r.Version is { } v && ReleaseVersion.Compare(v, current) == 0 && !r.IsEmpty);
    }
}
