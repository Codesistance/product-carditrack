namespace CardiTrack.Domain.Enums;

/// <summary>
/// What a stored <see cref="Entities.MemberInsight"/> is about. One table with a discriminator
/// rather than three, because the read path, the servability rule, the retention sweep and the
/// erasure cascade are identical for all three — the only thing that differs is what the prompt
/// was given and how often it is worth regenerating.
/// </summary>
public enum InsightScope
{
    /// <summary>
    /// One alert explained: what it means in the recent readings, and one thing the caregiver can
    /// do now. Keyed to its alert, written in the pass that raised it, never regenerated — the
    /// alert it explains does not change after the fact.
    /// </summary>
    Alert = 1,

    /// <summary>
    /// The member's readings against their own learned baseline, with the learning and provisional
    /// states the dashboard already shows. One row per member, rewritten as the picture moves.
    /// </summary>
    Baseline = 2,

    /// <summary>
    /// The longer-horizon trend narrative from the daily trend pass — moving averages, slopes and
    /// deviations computed in .NET, read by the model against the pinned reference ranges. One row
    /// per member. Absent while a member has fewer than
    /// <c>TrendFeatureCalculator.MinimumDaysForTrend</c> days of readings: there is no trajectory
    /// to narrate yet, and saying so is the learning state, not a trend.
    /// </summary>
    Trend = 3,

    /// <summary>
    /// The same read at <see cref="TrendHorizon.Weekly"/>: the seven days ending the evening before
    /// the member's own week start, written once their Weekbook hour passes.
    /// </summary>
    /// <remarks>
    /// Its own scope rather than a horizon column on <see cref="Trend"/>, because the unique index
    /// this table already carries is (member, scope) filtered to the rows with no alert — so a new
    /// scope is one row per member for free, where a horizon column would mean altering that index
    /// and the partial one beside it. The three trend scopes share every other property the type
    /// remarks list, which is the condition for sharing the table at all.
    /// </remarks>
    TrendWeekly = 4,

    /// <summary>
    /// The same read at <see cref="TrendHorizon.Monthly"/>: the month just gone, written on the
    /// first of the next once the member's Monthbook hour passes.
    /// </summary>
    TrendMonthly = 5,
}
