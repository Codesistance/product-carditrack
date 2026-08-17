using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Questionnaires;

/// <summary>
/// Whether a question is still worth putting in front of a caregiver.
/// </summary>
/// <remarks>
/// <para>
/// A question about the present moment — "did he feel tired at all today?" — stops making sense when
/// that moment passes, and a card that asks it anyway is worse than no card: it asks the family
/// about a day that has ended, and every answer it collects is filed against the wrong one.
/// </para>
/// <para>
/// The rule lives here, apart from any page, for the same reason as the rest of
/// <see cref="MemberQuestionnaires"/>: it is a rule about a questionnaire rather than about a
/// screen, three surfaces need the identical verdict, and it is testable without a UI. The server
/// enforces the same rule on the way out — this is not the app being trusted with the decision, it
/// is the app being able to tell without a round trip.
/// </para>
/// </remarks>
public static class QuestionValidity
{
    /// <summary>
    /// How far the device's clock may run ahead of the server's before this stops believing it.
    /// </summary>
    /// <remarks>
    /// Phone clocks drift, and a user can set one by hand. Without a margin, a device an hour fast
    /// would quietly retire every question in its final hour for the whole family — the server
    /// re-checks before acting, so the damage is bounded to a card this app declines to draw, but
    /// declining to draw a live question is still the wrong answer. The margin costs the opposite
    /// case a few minutes of a stale card, which the next refresh clears anyway.
    /// </remarks>
    public static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(10);

    /// <summary>
    /// True when this question has outlived the moment it asked about and should neither be drawn
    /// nor answered.
    /// </summary>
    /// <param name="questionnaire">The question, as the API returned it.</param>
    /// <param name="utcNow">Now, in UTC. Taken rather than read so this stays testable.</param>
    /// <remarks>
    /// A null <c>AskableUntilUtc</c> is valid indefinitely, which covers both a standing-fact
    /// question and any row written before questions carried a validity at all — the same reading
    /// the server gives it.
    /// </remarks>
    public static bool HasLapsed(QuestionnaireResponse questionnaire, DateTime utcNow) =>
        questionnaire.AskableUntilUtc is { } until && until <= utcNow - ClockSkewAllowance;

    /// <summary>
    /// The pending question worth showing, or null when there is none — either because the API
    /// returned none, or because the one it returned has since lapsed.
    /// </summary>
    /// <remarks>
    /// The second case is the one this exists for. A page holds a card while the day rolls over; a
    /// phone offline for a morning is served yesterday's snapshot out of the read cache, which keeps
    /// a response for up to a week. Both hand the app a question the server would no longer serve.
    /// </remarks>
    public static QuestionnaireResponse? StillWorthAsking(
        QuestionnaireResponse? pending, DateTime utcNow) =>
        pending is not null && !HasLapsed(pending, utcNow) ? pending : null;
}
