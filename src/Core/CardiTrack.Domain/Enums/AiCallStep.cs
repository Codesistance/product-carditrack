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

    /// <summary>The routing classification — which catalogue entry serves the question. Billed to
    /// the turn in shadow mode too: the call ran, and a usage row that skipped it would make the
    /// shadow period look free.</summary>
    Route = 6,
}
