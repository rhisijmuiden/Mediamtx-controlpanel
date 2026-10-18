using MediaMtxPanel.Services;

namespace MediaMtxPanel.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.3.0", "1.2.0")]
    [InlineData("v1.2.1", "1.2.0")]
    [InlineData("v1.3.0", "1.3.0-test.4")]
    [InlineData("v1.3.0-test.2", "1.3.0-test.1")]
    [InlineData("v1.3.0-test.10", "1.3.0-test.9")]
    [InlineData("v1.3.0-test.1", "1.2.0")]
    [InlineData("v1.3.0-rc.1", "1.3.0-beta.2")]
    [InlineData("v1.3.0-test.1.1", "1.3.0-test.1")]
    public void Newer(string candidate, string current) => Assert.True(ReleaseVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v1.2.0-test.3", "1.2.0")]
    [InlineData("v1.1.9", "1.2.0")]
    [InlineData("nightly", "1.2.0")]
    public void Not_newer(string candidate, string current) => Assert.False(ReleaseVersion.IsNewer(candidate, current));
}
