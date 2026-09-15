namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// How often a screen re-reads the cards the AI pipeline writes — the family summary, the
/// wellness suggestion, the questions waiting to be answered — as opposed to the member's own
/// current state, which moves far faster and keeps the screen's ordinary live cadence.
/// </summary>
/// <remarks>
/// <para>
/// The member detail screen used to refetch all four on the same thirty-second tick. Three of
/// them cannot have changed in thirty seconds: the pipeline's digest job runs half-hourly and
/// again after each assessor pass, and the assessor runs every five minutes, so five minutes is
/// the floor on how often any of this can be rewritten at all. A regeneration floor then admits
/// at most three ordinary summaries an hour on top of that. Polling faster than the fastest
/// possible write buys nothing and is paid for per minute of watching, on a screen caregivers
/// leave open.
/// </para>
/// <para>
/// Lives in Mobile.Core rather than beside <c>PeriodicRefresh</c> in Mobile so the decision can
/// be exercised directly by the unit tests, which reference Core and not the MAUI head.
/// </para>
/// </remarks>
public static class GeneratedContentRefresh
{
    /// <summary>
    /// The gap an unattended pass must clear before it re-reads the generated cards. Matched to
    /// the assessor cadence above, not chosen for its own sake: it is the interval below which
    /// there is provably nothing new to fetch.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether this pass should re-read the generated cards.
    /// </summary>
    /// <param name="requestedByCaregiver">
    /// True for an arrival, a pull-to-refresh or a retry — anything the caregiver did on
    /// purpose. Those always re-read, whatever the clock says: someone who pulls the screen down
    /// is entitled to be told that nothing moved, and a refresh that quietly skipped most of the
    /// page would make the gesture a lie.
    /// </param>
    /// <param name="lastReadUtc">
    /// When this screen last read them. <see cref="DateTime.MinValue"/> for a screen that never
    /// has, which is always due.
    /// </param>
    public static bool IsDue(bool requestedByCaregiver, DateTime lastReadUtc, DateTime nowUtc) =>
        requestedByCaregiver || nowUtc - lastReadUtc >= Interval;
}
