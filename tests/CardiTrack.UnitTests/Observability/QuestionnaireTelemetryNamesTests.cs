using CardiTrack.Application.Diagnostics;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.UnitTests.Observability;

/// <summary>
/// Pins the wiring contract between Application (which defines the questionnaire meter)
/// and Observability (which registers it for export by name via AddMeter). The projects
/// share only the TelemetryNames constant; if either side drifts from it, the funnel
/// silently stops shipping — these tests make that drift a test failure instead.
/// </summary>
public class QuestionnaireTelemetryNamesTests
{
    [Fact]
    public void Meter_UsesTheSharedName()
        => Assert.Equal(TelemetryNames.QuestionnaireSource, QuestionnaireTelemetry.Meter.Name);

    /// <summary>Dashboards and monitors key on these instrument names — renames break them.</summary>
    [Fact]
    public void InstrumentNames_AreTheFunnelCounters()
    {
        Assert.Equal("questionnaire.asked", QuestionnaireTelemetry.Asked.Name);
        Assert.Equal("questionnaire.answered", QuestionnaireTelemetry.Answered.Name);
        Assert.Equal("questionnaire.dismissed", QuestionnaireTelemetry.Dismissed.Name);
        Assert.Equal("questionnaire.expired", QuestionnaireTelemetry.Expired.Name);
        Assert.Equal("questionnaire.offered", QuestionnaireTelemetry.Offered.Name);
        Assert.Equal("questionnaire.digest.informed", QuestionnaireTelemetry.DigestInformed.Name);
        Assert.Equal("questionnaire.digest.recited", QuestionnaireTelemetry.DigestRecited.Name);
    }
}
