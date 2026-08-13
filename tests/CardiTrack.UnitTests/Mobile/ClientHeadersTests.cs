using CardiTrack.Mobile.Core.Http;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The client half of the version-header contract. What matters is that a build never puts a value
/// on the wire the API would refuse — the API drops silently, so a mismatch here would show up as
/// telemetry that is simply missing rather than as any kind of failure.
/// </summary>
public class ClientHeadersTests
{
    [Fact]
    public void FormatVersion_JoinsDisplayVersionAndBuildNumber()
    {
        Assert.Equal("1.4.2+37", ClientHeaders.FormatVersion("1.4.2", "37"));
    }

    /// <summary>
    /// The build number is the optional half: an unstamped local build still identifies its
    /// display version, just not the individual CI run.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FormatVersion_OmitsBuildNumberWhenAbsent(string? buildNumber)
    {
        Assert.Equal("1.4.2", ClientHeaders.FormatVersion("1.4.2", buildNumber));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FormatVersion_ReturnsNullWithoutADisplayVersion(string? displayVersion)
    {
        Assert.Null(ClientHeaders.FormatVersion(displayVersion, "37"));
    }

    [Fact]
    public void FormatVersion_KeepsPrereleaseTags()
    {
        Assert.Equal("2.0.0-rc.1+88", ClientHeaders.FormatVersion("2.0.0-rc.1", "88"));
    }

    /// <summary>
    /// Nothing legitimate produces these, but a value that can't survive the API's validation is
    /// better dropped than sent: an absent header reads as "unknown build", which is honest, while
    /// a rejected one would look identical and cost a round trip's worth of header.
    /// </summary>
    [Theory]
    [InlineData("1.0 beta", "1")]
    [InlineData("1.0\nInjected", "1")]
    [InlineData("1.0", "37 38")]
    public void FormatVersion_ReturnsNullForValuesTheApiWouldReject(string displayVersion, string buildNumber)
    {
        Assert.Null(ClientHeaders.FormatVersion(displayVersion, buildNumber));
    }

    [Fact]
    public void FormatVersion_ReturnsNullWhenTooLongToRecord()
    {
        Assert.Null(ClientHeaders.FormatVersion(new string('1', 65), null));
    }

    /// <summary>
    /// Case is folded on the client so a single platform can never appear under two spellings in
    /// the same APM query.
    /// </summary>
    [Theory]
    [InlineData("Android", "android")]
    [InlineData("iOS", "ios")]
    [InlineData("WinUI", "winui")]
    [InlineData("  Android  ", "android")]
    public void NormalizePlatform_LowercasesTheMauiPlatformName(string platform, string expected)
    {
        Assert.Equal(expected, ClientHeaders.NormalizePlatform(platform));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("android phone")]
    public void NormalizePlatform_ReturnsNullWhenThereIsNothingUsable(string? platform)
    {
        Assert.Null(ClientHeaders.NormalizePlatform(platform));
    }
}
