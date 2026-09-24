using System.Diagnostics.Metrics;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.Infrastructure.Diagnostics;

/// <summary>
/// What became of each candidate member on one CardiJournal pass, by book. Written, or why not.
/// </summary>
/// <remarks>
/// <para>
/// The books are written by a pass that runs every half hour over every member with recent
/// readings, and on nearly every one of those passes nearly every member is declined: not due
/// yet, already written, no readings for the period. Every one of those exits used to be a bare
/// <c>return false</c>, and the completion line was written only when a book had been written —
/// so a pass that declined everyone left no trace at all, and a member who never got a Daybook
/// could not be told apart from one whose pass never ran. The 2026-09-24 walk-through of this
/// pass found four candidates and one book, and could not say where the other three went.
/// </para>
/// <para>
/// This counter is the answer to that at dashboard scale, beside <see cref="CopyGuardTelemetry"/>
/// which already counts the discards; the completion line carries the same tally per pass for
/// the log reader. The outcomes are bounded on purpose: a reason, never a member id, a date or
/// anything read from the period.
/// </para>
/// </remarks>
public static class JournalPassTelemetry
{
    public static readonly Meter Meter = new(TelemetryNames.PipelineSource);

    public static readonly Counter<long> Outcomes = Meter.CreateCounter<long>(
        "carditrack.journal.outcome",
        description: "Candidate members on a CardiJournal pass, by book and what became of them");

    /// <summary>Which book the pass was writing: <c>daybook</c>, <c>weekbook</c> or <c>monthbook</c>.</summary>
    public const string BookTag = "journal.book";

    /// <summary>What became of the member — one of the <see cref="JournalPassOutcome"/> names.</summary>
    public const string OutcomeTag = "journal.outcome";

    /// <summary>Records one member's outcome on one book's pass.</summary>
    public static void Count(string book, JournalPassOutcome outcome) =>
        Outcomes.Add(
            1,
            new KeyValuePair<string, object?>(BookTag, book),
            new KeyValuePair<string, object?>(OutcomeTag, Name(outcome)));

    /// <summary>The tag value for an outcome: snake_case, stable, and the same word the
    /// completion line uses for it.</summary>
    public static string Name(JournalPassOutcome outcome) => outcome switch
    {
        JournalPassOutcome.Written => "written",
        JournalPassOutcome.NotDue => "not_due",
        JournalPassOutcome.AlreadyWritten => "already_written",
        JournalPassOutcome.MemberUnavailable => "member_unavailable",
        JournalPassOutcome.NoReadings => "no_readings",
        JournalPassOutcome.ClaimedElsewhere => "claimed_elsewhere",
        JournalPassOutcome.Discarded => "discarded",
        JournalPassOutcome.WriteRefused => "write_refused",
        JournalPassOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a journal pass outcome."),
    };
}

/// <summary>
/// The one thing a book's pass decided about one candidate member. Ordered roughly as the pass
/// itself decides them, so a reader of the tally sees the gates in the order they were applied.
/// </summary>
public enum JournalPassOutcome
{
    /// <summary>The book was composed, passed every guard and was stored.</summary>
    Written,

    /// <summary>The member's local clock has not reached the book's hour, or it is not the day
    /// this book is written on.</summary>
    NotDue,

    /// <summary>A book for the period already exists.</summary>
    AlreadyWritten,

    /// <summary>The member row is missing, inactive, or their monitoring is paused.</summary>
    MemberUnavailable,

    /// <summary>The period carried no readings, or too few days of them for this book.</summary>
    NoReadings,

    /// <summary>Another execution holds the lease on this period; left to them.</summary>
    ClaimedElsewhere,

    /// <summary>The model replied and a guard refused the reply; nothing stored, retried next pass.</summary>
    Discarded,

    /// <summary>Composed and guarded, but the insert stored nothing: another execution's row won
    /// the unique index, or the member is under an erasure hold.</summary>
    WriteRefused,

    /// <summary>An exception escaped the member's generation; logged and retried next pass.</summary>
    Failed,
}

/// <summary>A per-pass count of <see cref="JournalPassOutcome"/>s, for the completion line.</summary>
public sealed class JournalPassTally
{
    private readonly int[] _counts = new int[Enum.GetValues<JournalPassOutcome>().Length];

    public void Add(JournalPassOutcome outcome) => _counts[(int)outcome]++;

    public int this[JournalPassOutcome outcome] => _counts[(int)outcome];
}
