namespace CardiTrack.Domain.Enums;

/// <summary>Where a generated question has got to.</summary>
public enum QuestionnaireStatus
{
    /// <summary>Asked, not yet answered. At most one of these exists per member at a time.</summary>
    Pending = 1,

    /// <summary>The family answered. The answer becomes context for later generations.</summary>
    Answered = 2,

    /// <summary>
    /// The family skipped it. Distinct from deleted: the question is never shown or asked again, but
    /// the record of having asked survives, which is what stops the same ground being covered twice.
    /// </summary>
    Dismissed = 3,

    /// <summary>
    /// The moment it asked about has passed, so it stopped being worth asking before anyone got to
    /// it — see <see cref="CardiTrack.Domain.Entities.MemberQuestionnaire.AskableUntilUtc"/>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Dismissed"/>, which is the family's own decision and a promise never
    /// to ask that again. This one is nobody's decision and carries no such promise: the same ground
    /// may be worth covering tomorrow, on tomorrow's readings. Distinct from <see cref="Pending"/>
    /// because a pending row gates every future question for that member — an expired question left
    /// pending gags the feature indefinitely.
    /// </remarks>
    Expired = 4,
}
