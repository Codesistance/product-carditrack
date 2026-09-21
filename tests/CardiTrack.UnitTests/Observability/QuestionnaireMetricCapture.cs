using System.Diagnostics.Metrics;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.UnitTests.Observability;

/// <summary>
/// Serialises every test class that records to, or listens on, the questionnaire meter.
/// </summary>
/// <remarks>
/// <para>
/// <c>QuestionnaireTelemetry.Meter</c> is a <c>static readonly</c> singleton, and a
/// <see cref="MeterListener"/> can only select instruments by meter <em>name</em> — there is no
/// per-test meter instance to filter on. So while two of these classes run in parallel, each
/// one's <see cref="QuestionnaireMetricCapture"/> receives the other's measurements.
/// </para>
/// <para>
/// That produced two distinct intermittent failures before this collection existed: a
/// <c>before</c>/<c>after</c> count assertion seeing a count it did not cause, and
/// <c>Assert.Contains</c> throwing "Collection was modified" because it enumerates the captured
/// list while a parallel class's measurement is being appended. Both looked like one-off red
/// tests and passed on a re-run, which is the expensive kind of flake — local <c>dotnet test</c>
/// is the only gate this repo has.
/// </para>
/// <para>
/// Membership is therefore mandatory, not a nicety: a class that emits questionnaire metrics
/// poisons the others' captures even if it never constructs a capture itself. This mirrors the
/// "AiTelemetry" collection, which exists for the same reason on <c>CardiTrack.Ai</c>.
/// </para>
/// </remarks>
[CollectionDefinition(QuestionnaireTelemetryCollection.Name)]
public class QuestionnaireTelemetryCollection
{
    public const string Name = "QuestionnaireTelemetry";
}

/// <summary>
/// Captures <see cref="long"/> measurements from the questionnaire meter only (BCL
/// <see cref="MeterListener"/>).
/// </summary>
/// <remarks>
/// Use this only from a test class that belongs to
/// <see cref="QuestionnaireTelemetryCollection"/>. Outside that collection the capture also
/// receives whatever a test class running in parallel records, which silently invalidates any
/// assertion that counts measurements — see the collection for the full explanation.
/// </remarks>
public sealed class QuestionnaireMetricCapture : IDisposable
{
    private readonly MeterListener _listener = new();

    private readonly List<(string Instrument, long Value, Dictionary<string, object?> Tags)> _longs = [];

    /// <summary>
    /// An isolated snapshot, copied under the lock on every read. The measurement callback runs on
    /// whichever thread recorded the metric, so handing out the live list would let an assertion
    /// enumerate it mid-append; a copy per read is what makes that impossible.
    /// </summary>
    public IReadOnlyList<(string Instrument, long Value, Dictionary<string, object?> Tags)> Longs
    {
        get { lock (_longs) return _longs.ToArray(); }
    }

    public QuestionnaireMetricCapture()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == TelemetryNames.QuestionnaireSource)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var dictionary = new Dictionary<string, object?>();
            foreach (var tag in tags)
                dictionary[tag.Key] = tag.Value;

            lock (_longs)
                _longs.Add((instrument.Name, value, dictionary));
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();
}
