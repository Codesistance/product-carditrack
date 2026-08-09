using CardiTrack.Infrastructure.Diagnostics;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.UnitTests.Observability;

/// <summary>
/// Pins the wiring contract between CardiTrack.Infrastructure (which defines the AI
/// ActivitySource/Meter) and CardiTrack.Observability (which registers them for export by
/// name via AddSource/AddMeter). The projects share only the TelemetryNames constant; if
/// either side drifts from it, spans and metrics silently stop shipping — these tests make
/// that drift a test failure instead.
/// </summary>
public class AiTelemetryNamesTests
{
    [Fact]
    public void ActivitySource_UsesTheSharedName()
        => Assert.Equal(TelemetryNames.AiSource, AiTelemetry.Source.Name);

    [Fact]
    public void Meter_UsesTheSharedName()
        => Assert.Equal(TelemetryNames.AiSource, AiTelemetry.Meter.Name);

    /// <summary>Dashboards and monitors key on these instrument names — renames break them.</summary>
    [Fact]
    public void InstrumentNames_FollowGenAiSemanticConventions()
    {
        Assert.Equal("gen_ai.client.operation.duration", AiTelemetry.OperationDuration.Name);
        Assert.Equal("s", AiTelemetry.OperationDuration.Unit);
        Assert.Equal("gen_ai.client.token.usage", AiTelemetry.TokenUsage.Name);
        Assert.Equal("{token}", AiTelemetry.TokenUsage.Unit);
    }
}
