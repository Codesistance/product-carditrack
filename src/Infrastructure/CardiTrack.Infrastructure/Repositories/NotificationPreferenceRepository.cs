using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class NotificationPreferenceRepository : Repository<NotificationPreference>, INotificationPreferenceRepository
{
    public NotificationPreferenceRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<NotificationPreference?> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        await _dbSet.FirstOrDefaultAsync(p => p.UserId == userId, ct);
}
