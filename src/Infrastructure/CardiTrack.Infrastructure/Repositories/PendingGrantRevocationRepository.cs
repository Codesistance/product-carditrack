using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class PendingGrantRevocationRepository
    : Repository<PendingGrantRevocation>, IPendingGrantRevocationRepository
{
    public PendingGrantRevocationRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<PendingGrantRevocation>> GetDueAsync(
        DateTime utcNow, int max, CancellationToken ct = default) =>
        await _dbSet
            .Where(r => r.NextAttemptAt <= utcNow)
            .OrderBy(r => r.NextAttemptAt)
            .Take(max)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PendingGrantRevocation>> GetByCardiMemberIdAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        await _dbSet.Where(r => r.CardiMemberId == cardiMemberId).ToListAsync(ct);
}
