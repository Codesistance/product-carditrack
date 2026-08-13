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
}
