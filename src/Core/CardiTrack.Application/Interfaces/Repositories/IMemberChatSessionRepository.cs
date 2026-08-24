using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>Access to one caregiver's chat conversations about a CardiMember.</summary>
public interface IMemberChatSessionRepository : IRepository<MemberChatSession>
{
    /// <summary>
    /// The caregiver's most recently active session for this member, if one exists — what a new
    /// message continues rather than starting fresh. "Active" means not explicitly ended and
    /// last-active since the cutoff; the window itself is a client/product decision, not encoded
    /// here — the caller passes it.
    /// </summary>
    Task<MemberChatSession?> GetActiveAsync(
        Guid userId, Guid cardiMemberId, DateTime activeSinceUtc, CancellationToken ct = default);

    /// <summary>The session and its turns, oldest first — what the history endpoint and each new
    /// turn's prompt-history both read.</summary>
    Task<MemberChatSession?> GetByIdWithTurnsAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// The caregiver's completed conversations about this member — every session except the one
    /// still active by <paramref name="activeSinceUtc"/>'s window — newest started first, each
    /// with the facts the history list renders (opening question, question count, stored theme),
    /// computed in SQL so listing a long history never loads whole threads.
    /// </summary>
    Task<IReadOnlyList<MemberChatSessionListing>> ListCompletedForMemberAsync(
        Guid userId, Guid cardiMemberId, DateTime activeSinceUtc, CancellationToken ct = default);
}

/// <summary>One row of <see cref="IMemberChatSessionRepository.ListCompletedForMemberAsync"/>.</summary>
public sealed record MemberChatSessionListing
{
    public required MemberChatSession Session { get; init; }

    /// <summary>The first caregiver turn's content as stored — still encrypted; the service
    /// decrypts. Null on a session that never got a caregiver turn.</summary>
    public required string? FirstQuestionContent { get; init; }

    /// <summary>Caregiver turns only — "3 questions" is what the list says, and counting the
    /// replies would double it.</summary>
    public required int QuestionCount { get; init; }
}
