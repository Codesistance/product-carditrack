using CardiTrack.Domain.Common;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One caregiver's ongoing conversation about one CardiMember. The first persisted, multi-turn
/// clinical conversation on this platform — every other AI surface is either never-persisted (the
/// on-demand insight endpoints) or de-identified (the general chat provider). See
/// docs/compliance/dpia.md for the processing-operation entry and retention proposal this entails.
/// </summary>
/// <remarks>
/// Deliberately not <c>ISoftDeletable</c>, for the same reason as <see cref="MemberQuestionnaire"/>:
/// the content is a record of a caregiver asking about a person who never consented to the service,
/// and "delete" here has to mean the rows are gone (GDPR Art. 17), not flagged.
/// </remarks>
public class MemberChatSession : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    /// <summary>The caregiver having this conversation.</summary>
    public Guid UserId { get; set; }

    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// Updated on every turn. Drives "get or continue the active session" — a caregiver reopening
    /// chat within the same conversation window continues it rather than starting a new one.
    /// </summary>
    public DateTime LastTurnAtUtc { get; set; }

    /// <summary>Loaded only by <c>IMemberChatSessionRepository.GetByIdWithTurnsAsync</c> — every
    /// other read goes through <see cref="MemberChatTurn"/> directly.</summary>
    public ICollection<MemberChatTurn> Turns { get; set; } = new List<MemberChatTurn>();
}
