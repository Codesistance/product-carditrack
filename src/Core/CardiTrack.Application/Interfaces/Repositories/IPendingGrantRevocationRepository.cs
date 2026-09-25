using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IPendingGrantRevocationRepository : IRepository<PendingGrantRevocation>
{
    /// <summary>Revocations due by <paramref name="utcNow"/>, oldest due first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<PendingGrantRevocation>> GetDueAsync(DateTime utcNow, int max, CancellationToken ct = default);

    /// <summary>A member's queued revocations, for erasure to end before deleting them.</summary>
    Task<IReadOnlyList<PendingGrantRevocation>> GetByCardiMemberIdAsync(Guid cardiMemberId, CancellationToken ct = default);
}
