using CardiTrack.Mobile.Core.Diagnostics;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The native Datadog SDKs use a custom endpoint verbatim, so what leaves here has to be the
/// full per-feature URL: a bare host posts to the site root and every batch comes back 404.
/// </summary>
public class DatadogIntakeTests
{
    [Fact]
    public void TryCreate_ComposesTheFullPerFeatureUrls()
    {
        Assert.True(DatadogIntake.TryCreate("browser-intake-uk1-datadoghq.com", out var intake));
        Assert.Equal("https://browser-intake-uk1-datadoghq.com/api/v2/rum", intake!.Rum);
        Assert.Equal("https://browser-intake-uk1-datadoghq.com/api/v2/logs", intake.Logs);
        Assert.Equal("https://browser-intake-uk1-datadoghq.com/api/v2/spans", intake.Traces);
    }

    [Fact]
    public void TryCreate_TrimsSurroundingWhitespace()
    {
        Assert.True(DatadogIntake.TryCreate("  browser-intake-uk1-datadoghq.com  ", out var intake));
        Assert.Equal("browser-intake-uk1-datadoghq.com", intake!.Host);
    }

    /// <summary>
    /// A scheme or path would double up with what the app composes, and a port or anything
    /// else that is not a host name has no place here — better off than misrouted.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://browser-intake-uk1-datadoghq.com")]
    [InlineData("browser-intake-uk1-datadoghq.com/api/v2/rum")]
    [InlineData("browser-intake-uk1-datadoghq.com:443")]
    [InlineData("not a host")]
    public void TryCreate_RejectsAnythingButABareHostName(string? host)
    {
        Assert.False(DatadogIntake.TryCreate(host, out var intake));
        Assert.Null(intake);
    }
}
