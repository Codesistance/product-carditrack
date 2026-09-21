namespace CardiTrack.Domain.Enums;

/// <summary>
/// Which once-per-period generation a <see cref="Entities.GenerationLease"/> is held for. One
/// value per writer that rides the half-hourly digest pass and pays for a model call it must not
/// pay for twice.
/// </summary>
/// <remarks>
/// <para>
/// Persisted by name, following <c>MemberAiHoldConfiguration.Purpose</c>: a human reading a held
/// row should see which writer holds it without consulting a table of numbers, and a renumbering
/// must not silently re-point a lease at a different writer.
/// </para>
/// <para>
/// The family summary and Advise are deliberately absent. Both are written repeatedly through
/// the day by design — a summary is rewritten as the day moves and Advise up to five times in
/// the waking window — so there is no period for a lease to cover: a second write of either is
/// the feature working, not a duplicate.
/// </para>
/// </remarks>
public enum GenerationWork
{
    /// <summary>One finished day's CardiJournal book.</summary>
    Daybook = 1,

    /// <summary>One finished week's CardiJournal book.</summary>
    Weekbook = 2,

    /// <summary>One finished calendar month's CardiJournal book.</summary>
    Monthbook = 3,

    /// <summary>The weekly trend narrative, due beside the Weekbook.</summary>
    TrendWeekly = 4,

    /// <summary>The monthly trend narrative, due beside the Monthbook.</summary>
    TrendMonthly = 5,
}
