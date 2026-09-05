namespace CardiTrack.Application.Services;

/// <summary>
/// How stale a persisted <c>MemberStatusLine</c> may be before it is treated as though generation
/// has stopped for that member. Shared by <c>HealthInsightService.GetCurrentStatusMessageAsync</c>
/// (the dashboard hero card) and <c>MemberChatService</c>'s status rung, so the header a caregiver
/// is looking at and the answer chat gives them cannot disagree about whether there is a current
/// line.
/// </summary>
/// <remarks>
/// Lifted out of <c>HealthInsightService</c>, where it was a private constant with one reader. That
/// was fine while it had one; the moment chat became the second, it was the same setup
/// <see cref="AdviseStaleness"/> exists because of — two surfaces with their own idea of fresh,
/// which drifted apart once already and were flagged in PR #456's review.
/// </remarks>
public static class StatusLineStaleness
{
    /// <summary>
    /// Tighter than <see cref="AdviseStaleness.MaxAge"/> because the line regenerates on every
    /// digest and assess pass rather than roughly daily: a day-old row means generation has
    /// stopped for this member, and yesterday's reassurance presented as current would say
    /// something false. The dashboard's per-tier fallback copy — and, in chat, the readings
    /// themselves — is the honest answer then.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
}
