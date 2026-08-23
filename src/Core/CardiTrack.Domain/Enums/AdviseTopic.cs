namespace CardiTrack.Domain.Enums;

/// <summary>
/// Which area of the member's wellbeing a stored suggestion addresses — the key that lets
/// "does he need help with his sleep?" serve the sleep suggestion rather than whichever topic
/// the batch happened to write last. One <see cref="Entities.MemberAdvise"/> row per member per
/// topic; persisted by name, following <c>MemberChatTurnConfiguration.Workflow</c>.
/// </summary>
public enum AdviseTopic
{
    /// <summary>Spans areas, or predates topic scoping — every pre-migration row lands here,
    /// honestly: they were generated with no topic constraint.</summary>
    General = 1,

    Sleep = 2,

    Activity = 3,

    HeartRate = 4,
}
