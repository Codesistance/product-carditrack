using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class NotificationMuteRepository : Repository<NotificationMute>, INotificationMuteRepository
{
    public NotificationMuteRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<NotificationMute>> GetByUserAsync(
        Guid userId, CancellationToken ct = default)
        => await _dbSet
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.MutedDate)
            .ToListAsync(ct);

    public async Task<NotificationMute?> FindForScopeAsync(
        Guid userId, string? ruleCode, NotificationCategory? category,
        Guid? cardiMemberId, CancellationToken ct = default)
        => await _dbSet.FirstOrDefaultAsync(
            m => m.UserId == userId
                 && m.RuleCode == ruleCode
                 && m.Category == category
                 && m.CardiMemberId == cardiMemberId,
            ct);

    public async Task<int> ClearAllForUserAsync(Guid userId, CancellationToken ct = default)
        => await _dbSet.Where(m => m.UserId == userId).ExecuteDeleteAsync(ct);
}
