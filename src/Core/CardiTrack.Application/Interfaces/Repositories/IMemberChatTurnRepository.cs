using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>Access to individual chat turns, beyond what a session's own navigation loads.</summary>
public interface IMemberChatTurnRepository : IRepository<MemberChatTurn>
{
}
