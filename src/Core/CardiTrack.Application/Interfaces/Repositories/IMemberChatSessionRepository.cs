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
}
