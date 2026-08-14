namespace CardiTrack.Domain.Enums;

/// <summary>
/// How long an answer keeps informing future generations, as judged by the model that asked the
/// question — see <see cref="CardiTrack.Domain.Entities.MemberQuestionnaire.Scope"/>.
/// </summary>
public enum QuestionnaireScope
{
    /// <summary>
    /// Relevant only around when it was asked ("are they travelling this week?") — read back into
    /// prompts by recency, and left to age out once newer answers take its place. The default:
    /// unclassified rows (written before this distinction existed) behave as they always have.
    /// </summary>
    TimeScoped = 1,

    /// <summary>
    /// A standing fact about the member ("do they have a pacemaker?") that stays true regardless of
    /// how many newer answers arrive — read back into every future prompt until the family deletes
    /// it, not just the three most recent.
    /// </summary>
    Permanent = 2,
}
