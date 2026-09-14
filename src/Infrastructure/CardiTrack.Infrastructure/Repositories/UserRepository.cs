using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class UserRepository : Repository<User>, IUserRepository
{
    public UserRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<User?> GetByAuth0UserIdAsync(string auth0UserId)
    {
        return await _dbSet.FirstOrDefaultAsync(u => u.Auth0UserId == auth0UserId);
    }

    public async Task<User?> GetByEmailAsync(string email)
    {
        return await _dbSet.FirstOrDefaultAsync(u => u.Email == email);
    }

    public async Task UpdateLastLoginAsync(Guid userId)
    {
        var user = await GetByIdAsync(userId);
        if (user != null)
        {
            user.LastLoginDate = DateTime.UtcNow;
            Update(user);
        }
    }

    public async Task<bool> TryRecordHealthDataDisclosureDismissalAsync(string auth0UserId, DateTime dismissedAtUtc)
    {
        // One conditional UPDATE, not read-then-save: the WHERE is what makes the first
        // acknowledgement win when two devices race, and it runs without SaveChanges.
        var rows = await _dbSet
            .Where(u => u.Auth0UserId == auth0UserId && u.HealthDataDisclosureDismissedDate == null)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.HealthDataDisclosureDismissedDate, dismissedAtUtc));
        return rows > 0;
    }

    public async Task<bool> TryRequestDeletionAsync(string auth0UserId, DateTime requestedAtUtc)
    {
        // The null check is the whole point, not a guard against a race: it makes the first
        // request's timestamp the one the 30 days are counted from. A repeated tap that restarted
        // the clock would quietly hold the data longer than the caregiver was promised.
        var rows = await _dbSet
            .Where(u => u.Auth0UserId == auth0UserId && u.DeletionRequestedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.DeletionRequestedAtUtc, requestedAtUtc));
        return rows > 0;
    }

    public async Task<bool> TryCancelDeletionAsync(string auth0UserId)
    {
        var rows = await _dbSet
            .Where(u => u.Auth0UserId == auth0UserId && u.DeletionRequestedAtUtc != null)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.DeletionRequestedAtUtc, (DateTime?)null));
        return rows > 0;
    }

    public async Task<IReadOnlyList<Guid>> GetAccountsDueForErasureAsync(DateTime cutoffUtc, int limit)
    {
        // Inclusive of the cutoff: a window that closed exactly now has closed. Erring the other
        // way would leave an account waiting a whole extra run past a published thirty days.
        return await _dbSet
            .AsNoTracking()
            .Where(u => u.DeletionRequestedAtUtc != null && u.DeletionRequestedAtUtc <= cutoffUtc)
            .OrderBy(u => u.DeletionRequestedAtUtc)
            .Select(u => u.Id)
            .Take(limit)
            .ToListAsync();
    }
}
