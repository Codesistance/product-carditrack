namespace CardiTrack.Domain.Enums;

/// <summary>
/// Who put this row on file. The anti-fatigue clock and the "never ask the same wording
/// again" gate are about questions the service asked, not facts the family volunteered.
/// </summary>
public enum QuestionnaireOrigin
{
    /// <summary>
    /// The digest proposed this. Existing rows, and every row written before the family could
    /// volunteer a standing fact, are this.
    /// </summary>
    Digest = 1,

    /// <summary>
    /// The family told us a standing fact without being asked. Born answered; never pending,
    /// never pushed, and it does not count as an ask for the interval gate.
    /// </summary>
    Family = 2,
}
