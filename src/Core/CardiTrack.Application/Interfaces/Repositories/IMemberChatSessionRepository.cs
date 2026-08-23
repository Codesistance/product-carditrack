using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>Access to one caregiver's chat conversations about a CardiMember.</summary>
public interface IMemberChatSessionRepository : IRepository<MemberChatSession>
{
    /// <summary>
    /// The caregiver's most recently active session for this member, if one exists — what a new
    /// message continues rather than starting fresh. "Active" is a client/product decision (a
    /// session-length window), not encoded here; the caller passes the cutoff.
    /// </summary>
    Task<MemberChatSession?> GetActiveAsync(
        Guid userId, Guid cardiMemberId, DateTime activeSinceUtc, CancellationToken ct = default);

    /// <summary>The session and its turns, oldest first — what the history endpoint and each new
    /// turn's prompt-history both read.</summary>
    Task<MemberChatSession?> GetByIdWithTurnsAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Every session this caregiver has had about this member, newest activity first, each with
    /// the two facts the conversation list renders — the opening question and how many questions
    /// were asked — computed in SQL so listing a long history never loads whole threads.
    /// </summary>
    Task<IReadOnlyList<MemberChatSessionListing>> ListForMemberAsync(
        Guid userId, Guid cardiMemberId, CancellationToken ct = default);
}

/// <summary>One row of <see cref="IMemberChatSessionRepository.ListForMemberAsync"/>.</summary>
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
