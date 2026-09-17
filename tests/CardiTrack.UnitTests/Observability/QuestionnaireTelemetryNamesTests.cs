using System.Diagnostics.Metrics;
using CardiTrack.Application.Diagnostics;
using CardiTrack.Domain.Enums;
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

    [Fact]
    public void RecordAsked_EmitsTheScopeTag_AsAClosedVocabulary()
    {
        using var capture = new MetricCapture();

        QuestionnaireTelemetry.RecordAsked(QuestionnaireScope.Permanent);

        Assert.Contains(capture.Longs, m =>
            m.Instrument == "questionnaire.asked"
            && m.Value == 1
            && m.Tags.GetValueOrDefault(QuestionnaireTelemetry.ScopeTag) as string == "permanent"
            && !m.Tags.ContainsKey(QuestionnaireTelemetry.OriginTag));
    }

    [Fact]
    public void RecordAnswered_EmitsScopeAndOrigin_AsClosedVocabularies()
    {
        using var capture = new MetricCapture();

        QuestionnaireTelemetry.RecordAnswered(QuestionnaireScope.TimeScoped, QuestionnaireOrigin.Family);

        Assert.Contains(capture.Longs, m =>
            m.Instrument == "questionnaire.answered"
            && m.Value == 1
            && m.Tags.GetValueOrDefault(QuestionnaireTelemetry.ScopeTag) as string == "timescoped"
            && m.Tags.GetValueOrDefault(QuestionnaireTelemetry.OriginTag) as string == "family");
    }

    [Fact]
    public void RecordExpired_AddsTheCount_AndIgnoresAZeroPass()
    {
        using var capture = new MetricCapture();

        var expiredBefore = capture.Longs.Count(m => m.Instrument == "questionnaire.expired");
        QuestionnaireTelemetry.RecordExpired(0);
        Assert.Equal(expiredBefore, capture.Longs.Count(m => m.Instrument == "questionnaire.expired"));

        QuestionnaireTelemetry.RecordExpired(3);
        Assert.Contains(capture.Longs, m => m.Instrument == "questionnaire.expired" && m.Value == 3);
    }

    [Theory]
    [InlineData("questionnaire.dismissed")]
    [InlineData("questionnaire.offered")]
    [InlineData("questionnaire.digest.informed")]
    [InlineData("questionnaire.digest.recited")]
    public void UntaggedHelpers_EachEmitOneCount(string instrument)
    {
        using var capture = new MetricCapture();

        var before = capture.Longs.Count(m => m.Instrument == instrument);
        switch (instrument)
        {
            case "questionnaire.dismissed":
                QuestionnaireTelemetry.RecordDismissed();
                break;
            case "questionnaire.offered":
                QuestionnaireTelemetry.RecordOffered();
                break;
            case "questionnaire.digest.informed":
                QuestionnaireTelemetry.RecordDigestInformed();
                break;
            case "questionnaire.digest.recited":
                QuestionnaireTelemetry.RecordDigestRecited();
                break;
        }

        Assert.True(capture.Longs.Count(m => m.Instrument == instrument) >= before + 1);
        Assert.Contains(capture.Longs, m => m.Instrument == instrument && m.Value == 1 && m.Tags.Count == 0);
    }

    /// <summary>Captures measurements from the questionnaire meter only (BCL MeterListener).</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener = new();

        public List<(string Instrument, long Value, Dictionary<string, object?> Tags)> Longs { get; } = [];

        public MetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TelemetryNames.QuestionnaireSource)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (Longs) Longs.Add((instrument.Name, value, ToDictionary(tags)));
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();

        private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dictionary = new Dictionary<string, object?>();
            foreach (var tag in tags)
                dictionary[tag.Key] = tag.Value;
            return dictionary;
        }
    }
}
