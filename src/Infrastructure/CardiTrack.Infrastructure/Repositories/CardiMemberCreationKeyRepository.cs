using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    public async Task<bool> TryAddAsync(CardiMemberCreationKey key, CancellationToken ct = default)
    {
        await _dbSet.AddAsync(key, ct);
        try
        {
            await _context.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        })
        {
            return false;
        }
    }

    public Task PurgeOlderThanAsync(Guid userId, DateTime cutoffUtc, CancellationToken ct = default) =>
        _dbSet
            .Where(k => k.UserId == userId && k.CreatedDate < cutoffUtc)
            .ExecuteDeleteAsync(ct);
}
