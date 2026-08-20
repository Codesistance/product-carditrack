namespace CardiTrack.Domain.Enums;

/// <summary>
/// Which stage of the member-chat pipeline a <see cref="CardiTrack.Domain.Entities.MemberChatTurnUsage"/>
/// row's model call belongs to. One caregiver message spans up to four calls; this is what lets a
/// later cost read tell them apart instead of only totalling the turn.
/// </summary>
public enum AiCallStep
{
    /// <summary>The off-topic/malicious check run on the raw question, before anything else.</summary>
    MaliciousCheck = 1,

    /// <summary>Deciding which existing, whitelisted data sources the question needs.</summary>
    QueryPlan = 2,

    /// <summary>MedGemma's clinical read of the data the plan selected.</summary>
    ClinicalAnalysis = 3,

    /// <summary>Rewriting the clinical analysis into caregiver-plain-language prose.</summary>
    Rewrite = 4,
}
