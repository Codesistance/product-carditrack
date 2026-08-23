namespace CardiTrack.Domain.Enums;

/// <summary>
/// Which way of answering served one caregiver chat turn — the ladder in
/// <c>docs/technical/member_chat_routing.md</c> §2, plus the two off-ladder entries and the one
/// handler the router never returns.
/// </summary>
/// <remarks>
/// <para>
/// Persisted by <em>name</em>, not number — the convention <c>MemberChatTurnConfiguration.Role</c>
/// and <c>MemberQuestionnaireConfiguration.Status</c> already follow, because a name survives both
/// an incident and a renumbering. So the numbers here are free to move and the names are not:
/// renaming a member rewrites the meaning of every turn already stamped with it.
/// </para>
/// <para>
/// <see cref="Steer"/> is retired rather than removed for exactly that reason. It was the single
/// entry before the casual/off-topic split, and turns written under it stay honest only while the
/// name still resolves.
/// </para>
/// <para>
/// Until this is stamped, which workflow ran is only recoverable by inspecting which
/// <see cref="Entities.MemberChatTurnUsage"/> rows a turn has — and that cannot tell
/// <see cref="Status"/> from <see cref="Advise"/>, or a casual steer from an off-topic one, since
/// each pair makes the same calls. Storing it is what turns "which rung do caregivers actually
/// stand on?" into a query rather than a guess.
/// </para>
/// </remarks>
public enum MemberChatWorkflow
{
    /// <summary>One reading, or one moment. No comparison, no judgement, no model call.</summary>
    Status = 1,

    /// <summary>Computed over a window, against the member's own baseline and the published band.
    /// The default rung, and the failure target for everything unsure.</summary>
    Analysis = 2,

    /// <summary>A judgement on the findings, which must name what it rests on. A superset of
    /// <see cref="Analysis"/> — it returns the figures too.</summary>
    Inference = 3,

    /// <summary>Why something changed, and what co-occurred with it. The only entry that fetches
    /// twice.</summary>
    Investigation = 4,

    /// <summary>Serves the stored, grounded suggestion. The only entry licensed to recommend.
    /// </summary>
    Advise = 5,

    /// <summary>The single steer entry, as it stood before the casual/off-topic split. Retired for
    /// new turns; kept so turns written under it keep their meaning.</summary>
    Steer = 6,

    /// <summary>Not a question at all — a greeting, thanks, small talk, or a question about the
    /// assistant itself. Answered warmly.</summary>
    SteerCasual = 7,

    /// <summary>A genuine request, but about something unrelated to this person's health or care.
    /// Redirected gently.</summary>
    SteerOffTopic = 8,

    /// <summary>The router could not place the question, so the app asked which rung was meant.
    /// Never returned by the router — see the catalogue's routable flag.</summary>
    Clarify = 9,
}
