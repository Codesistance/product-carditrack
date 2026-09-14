using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class CardiMemberCreationKeyRepository
    : Repository<CardiMemberCreationKey>, ICardiMemberCreationKeyRepository
{
    public CardiMemberCreationKeyRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public Task<CardiMemberCreationKey?> FindAsync(
        Guid userId, string key, CancellationToken ct = default) =>
        _dbSet.AsNoTracking()
            .FirstOrDefaultAsync(k => k.UserId == userId && k.Key == key, ct);

    public Task PurgeOlderThanAsync(Guid userId, DateTime cutoffUtc, CancellationToken ct = default) =>
        _dbSet
            .Where(k => k.UserId == userId && k.CreatedDate < cutoffUtc)
            .ExecuteDeleteAsync(ct);
}
