namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// How often a screen re-reads the cards the AI pipeline writes — the family summary, the
/// wellness suggestion, the questions waiting to be answered — as opposed to the member's own
/// current state, which moves far faster and keeps the screen's ordinary live cadence.
/// </summary>
/// <remarks>
/// <para>
/// The member detail screen used to refetch all four on the same thirty-second tick. Three of
/// them cannot have changed in thirty seconds. The pipeline's assessor runs every five minutes
/// and the digest job runs behind it, so five minutes is the floor on how often any of this can
/// be rewritten at all. In the ordinary case it is far slower than that:
/// <c>DigestGenerationService.MinimumRegenerationInterval</c> holds a member to one summary an
/// hour. Only a waiver beats that floor — an alert raised or resolved, a yellow-or-above
/// real-time window or an SSA jump, or daily readings that diverge from the baseline — and a
/// waiver can land on any assessor pass, which is why this interval tracks the five minutes
/// rather than the hour.
/// </para>
/// <para>
/// Polling faster than the fastest possible write buys nothing, and it is paid for per minute of
/// watching on a screen caregivers leave open.
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
    /// there is provably nothing new to fetch, even for a waived regeneration.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long one screen's claim on a card holds another screen's read off, before the claim is
    /// treated as lost and the card is due again.
    /// </summary>
    /// <remarks>
    /// The point of a lease rather than a plain flag: a claim is released by the pass that took
    /// it, and a pass that never gets there — a request that hangs, a page faulted mid-load —
    /// would otherwise hold its card for the life of the app. A card nobody may read is a worse
    /// failure than the duplicate read the claim exists to prevent, so the claim expires on its
    /// own.
    /// Sized from the request budget it is covering: <c>TimeoutHandler</c> holds every ordinary
    /// call to thirty seconds, and the claim is only held across the member load, so a minute is
    /// twice as long as the thing it is waiting for can legitimately take. It is also well inside
    /// <see cref="Interval"/>, so a lost claim costs part of one window and never a whole one.
    /// </remarks>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(1);

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
