namespace CardiTrack.Domain.Enums;

/// <summary>
/// Which stage of the member-chat pipeline a <see cref="CardiTrack.Domain.Entities.MemberChatTurnUsage"/>
/// row's model call belongs to. One caregiver message spans up to four calls; this is what lets a
/// later cost read tell them apart instead of only totalling the turn. Values are DB-persisted;
/// never renumber.
/// </summary>
public enum AiCallStep
{
    /// <summary>The triage run on the raw question, before anything else — malicious, off-topic,
    /// casual, or a real health question.</summary>
    MaliciousCheck = 1,

    /// <summary>Deciding which existing, whitelisted data sources the question needs.</summary>
    QueryPlan = 2,

    /// <summary>MedGemma's clinical read of the data the plan selected.</summary>
    ClinicalAnalysis = 3,

    /// <summary>Rewriting the clinical analysis into caregiver-plain-language prose.</summary>
    Rewrite = 4,

    /// <summary>The short steer reply a casual or off-topic message gets instead of the full
    /// pipeline — one Rewrite-slot call, no clinical read.</summary>
    Steer = 5,

    /// <summary>The routing classification — which catalogue entry serves the question. Runs on
    /// every message and is billed to the turn whenever it ran; the one turn with no Route row is
    /// one where the routing call itself failed and the triage fallback answered.</summary>
    Route = 6,

    /// <summary>Reading an alert-settings request into a closed change plan — which rule or alarm,
    /// and what to do with it — on the Rewrite slot. The settings rung's one call after the route;
    /// the reply and the change itself are assembled and applied in code. Numbered clear of the
    /// journal steps (7 and 8) in flight on another branch when this shipped.</summary>
    SettingsPlan = 9,
}
