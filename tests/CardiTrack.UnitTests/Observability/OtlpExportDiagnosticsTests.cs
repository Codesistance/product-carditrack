using CardiTrack.Observability;
using Microsoft.Extensions.Logging;

namespace CardiTrack.UnitTests.Observability;

/// <summary>
/// The listener exists so that an OTLP export failing silently becomes a warning somebody sees.
/// That only works while the warnings are worth reading, so what is pinned here is which SDK
/// events are allowed to claim that level.
/// </summary>
public class OtlpExportDiagnosticsTests
{
    /// <summary>
    /// Raised once per instrument on a meter no provider subscribed, on every single start. It
    /// reports what this app chose to collect, not a fault — and at Warning it out-produced every
    /// real warning the API wrote.
    /// </summary>
    [Fact]
    public void LevelFor_KeepsMetricInstrumentIgnoredOutOfTheWarnings()
        => Assert.Equal(
            LogLevel.Debug,
            OtlpExportDiagnostics.LevelFor(OtlpExportDiagnostics.MetricInstrumentIgnoredEvent));

    /// <summary>
    /// Everything else stays at Warning, including events this listener has never seen: a new SDK
    /// event name is far more likely to be an export problem than more startup chatter, and the
    /// cost of guessing wrong in that direction is only noise.
    /// </summary>
    [Theory]
    [InlineData("ExporterErrorResult")]
    [InlineData("TrailingHeaderUnsupported")]
    [InlineData("SomeEventNameNobodyHasSeenYet")]
    [InlineData(null)]
    public void LevelFor_LeavesEverythingElseAtWarning(string? eventName)
        => Assert.Equal(LogLevel.Warning, OtlpExportDiagnostics.LevelFor(eventName));
}
