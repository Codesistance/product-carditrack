using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>Access to individual chat turns, beyond what a session's own navigation loads.</summary>
public interface IMemberChatTurnRepository : IRepository<MemberChatTurn>
{
    /// <summary>
    /// Takes the alert-settings proposal off one turn, atomically: one conditional update that
    /// succeeds only while the proposal is still there. True when this call took it; false when
    /// there was nothing to take — already claimed by an earlier answer, or by the same answer
    /// sent twice. The caller applies the change only on true, so a retried or concurrent
    /// "yes" cannot apply it twice. Commits immediately, outside the unit of work: a claim that
    /// waited for the turn's own commit would leave the race open until then.
    /// </summary>
    Task<bool> TryClaimPendingChangeAsync(Guid turnId, CancellationToken ct = default);
}
