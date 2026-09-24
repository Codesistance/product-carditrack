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
    public void TryCreate_TrimsAndLowercases()
    {
        Assert.True(DatadogIntake.TryCreate("  Browser-Intake-UK1-DatadogHQ.com  ", out var intake));
        Assert.Equal("browser-intake-uk1-datadoghq.com", intake!.Host);
    }

    [Theory]
    [InlineData("browser-intake-datadoghq.com")]
    [InlineData("browser-intake-us3-datadoghq.com")]
    [InlineData("browser-intake-datadoghq.eu")]
    [InlineData("browser-intake-ddog-gov.com")]
    [InlineData("browser-intake-us2-ddog-gov.com")]
    public void TryCreate_AcceptsDatadogsOwnIntakeHosts(string host)
    {
        Assert.True(DatadogIntake.TryCreate(host, out _));
    }

    /// <summary>
    /// Telemetry goes wherever this points. A scheme or path would double up with what the app
    /// composes, and any domain that is not Datadog's intake — however valid a host name —
    /// would hand logs and crash reports to someone else: better off than misrouted.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://browser-intake-uk1-datadoghq.com")]
    [InlineData("browser-intake-uk1-datadoghq.com/api/v2/rum")]
    [InlineData("browser-intake-uk1-datadoghq.com:443")]
    [InlineData("not a host")]
    [InlineData("telemetry.example.com")]
    [InlineData("browser-intake-uk1-datadoghq.com.example.com")]
    [InlineData("browser-intake-uk1-datadoghq.co")]
    [InlineData("evil-browser-intake-uk1-datadoghq.com")]
    public void TryCreate_RejectsAnythingButABareHostName(string? host)
    {
        Assert.False(DatadogIntake.TryCreate(host, out var intake));
        Assert.Null(intake);
    }
}
