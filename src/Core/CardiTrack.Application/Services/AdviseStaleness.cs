namespace CardiTrack.Application.Services;

/// <summary>
/// How stale a persisted Advise row (<c>MemberAdvise</c>) may be before it's treated as though
/// generation has stopped for that member. Shared by <c>HealthInsightService.GetAdviseAsync</c>
/// (withholds a stale row rather than serving it) and <c>DashboardService</c> (won't pulse for a
/// stale row) so the two surfaces can never disagree about what counts as fresh — flagged in
/// PR #456's review after the two started as separate constants and drifted out of sync with the
/// paused-member guard.
/// </summary>
public static class AdviseStaleness
{
    /// <summary>
    /// Wider than <c>StatusLineGenerationService</c>'s status-line ceiling because Advise
    /// regenerates roughly daily (<c>AdviseGenerationService.RegenerateIfDueAsync</c>'s own due
    /// window), not every batch pass — this is a buffer against one missed pass, not the cadence
    /// itself.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);
}
