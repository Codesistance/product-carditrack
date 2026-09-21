namespace CardiTrack.Domain.Enums;

/// <summary>
/// Which stretch of the member's own history a trend narrative reads. Selects both the spans the
/// deterministic features are computed over (<c>TrendWindow.For</c>) and the
/// <see cref="InsightScope"/> the result is stored under.
/// </summary>
/// <remarks>
/// <para>
/// The two journal horizons are deliberately the two the CardiJournal already writes books at,
/// and each falls due on the same local instant its book does — a week's trend and that week's
/// Weekbook describe the same seven days, and neither can drift from the other without
/// <c>JournalDueCheck</c> drifting first.
/// </para>
/// <para>
/// There is no daily horizon. A day is too short for a trajectory to be told from a Tuesday,
/// which is the same reason the Daybook counts against a baseline rather than fitting a line
/// through one day.
/// </para>
/// </remarks>
public enum TrendHorizon
{
    /// <summary>
    /// The rolling read the daily pass has always written: a week's average against a four-week
    /// slope, over a quarter of a year of history. Not aligned to any calendar boundary — it is
    /// "how they are going right now", refreshed daily.
    /// </summary>
    Rolling = 1,

    /// <summary>The seven days ending the evening before the member's own week start.</summary>
    Weekly = 2,

    /// <summary>The month just gone, read on the first of the next.</summary>
    Monthly = 3,
}
