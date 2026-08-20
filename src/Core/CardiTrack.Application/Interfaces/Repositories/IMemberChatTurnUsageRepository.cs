using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>Per-model-call token accounting. Read side exists for a future cost report; nothing
/// consumes it yet beyond that intent.</summary>
public interface IMemberChatTurnUsageRepository : IRepository<MemberChatTurnUsage>
{
    Task<IReadOnlyList<MemberChatTurnUsage>> GetByTurnIdsAsync(
        IReadOnlyCollection<Guid> turnIds, CancellationToken ct = default);
}
