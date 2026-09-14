using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class UserCardiMemberRepository : Repository<UserCardiMember>, IUserCardiMemberRepository
{
    public UserCardiMemberRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<UserCardiMember>> GetByUserIdAsync(Guid userId)
    {
        // Access checks and onboarding only need the link row. Including CardiMember here
        // loaded every linked member entity on every dashboard poll.
        return await _dbSet
            .AsNoTracking()
            .Where(ucm => ucm.UserId == userId)
            .ToListAsync();
    }

    public async Task<IEnumerable<UserCardiMember>> GetByCardiMemberIdAsync(Guid cardiMemberId)
    {
        return await _dbSet
            .Where(ucm => ucm.CardiMemberId == cardiMemberId)
            .Include(ucm => ucm.User)
            .ToListAsync();
    }

    public async Task<bool> IsLeftUnwatchedByPendingDeletionAsync(Guid cardiMemberId)
    {
        // Two halves, and both are load-bearing: the member must have an active watcher at all,
        // and none of those watchers may be staying. Written as the exact negation of the
        // condition DeviceConnectionRepository applies at its four sync-scheduling sites, because
        // a collection path that disagrees with the scheduler is worse than one that is simply
        // wrong — it stops one kind of collecting and not another for the same member.
        var hasActiveWatcher = await _dbSet
            .AsNoTracking()
            .AnyAsync(ucm => ucm.CardiMemberId == cardiMemberId && ucm.IsActive);

        if (!hasActiveWatcher)
            return false;

        return !await _dbSet
            .AsNoTracking()
            .AnyAsync(ucm => ucm.CardiMemberId == cardiMemberId
                             && ucm.IsActive
                             && _context.Users.Any(u => u.Id == ucm.UserId
                                                        && u.DeletionRequestedAtUtc == null));
    }
}
