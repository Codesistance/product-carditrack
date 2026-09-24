using System.Diagnostics.Metrics;
using CardiTrack.Infrastructure.Diagnostics;

namespace CardiTrack.UnitTests.Observability;

/// <summary>
/// Serialises every test class that records to, or listens on, the journal outcome counter.
/// </summary>
/// <remarks>
/// The same reasoning as <see cref="QuestionnaireTelemetryCollection"/>: the counter lives on a
/// <c>static readonly</c> meter, a <see cref="MeterListener"/> selects by name, and two classes
/// running in parallel would each receive the other's measurements. Every test class whose
/// subject runs a journal pass — the Weekbook and Monthbook generation tests today — belongs
/// here, whether or not it captures anything itself.
/// </remarks>
[CollectionDefinition(JournalTelemetryCollection.Name)]
public class JournalTelemetryCollection
{
    public const string Name = "JournalTelemetry";
}

/// <summary>
/// Captures <see cref="JournalPassTelemetry.Outcomes"/> measurements only, as (book, outcome)
/// pairs in the order they were recorded.
/// </summary>
public sealed class JournalMetricCapture : IDisposable
{
    private readonly MeterListener _listener = new();

    private readonly List<(string Book, string Outcome)> _outcomes = [];

    /// <summary>A copy taken under the lock, so an assertion never enumerates a list mid-append.</summary>
    public IReadOnlyList<(string Book, string Outcome)> Outcomes
    {
        get { lock (_outcomes) return _outcomes.ToArray(); }
    }

    public JournalMetricCapture()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == JournalPassTelemetry.Outcomes.Name
                && instrument.Meter.Name == JournalPassTelemetry.Meter.Name)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? book = null, outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == JournalPassTelemetry.BookTag)
                    book = tag.Value as string;
                else if (tag.Key == JournalPassTelemetry.OutcomeTag)
                    outcome = tag.Value as string;
            }

            lock (_outcomes)
                _outcomes.Add((book ?? "", outcome ?? ""));
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();
}
