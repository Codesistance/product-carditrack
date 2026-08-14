using System.Linq.Expressions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class NotificationRepository : Repository<Notification>, INotificationRepository
{
    /// <summary>
    /// Sort key for urgency. <see cref="Notification.Priority"/> is persisted as text (see
    /// <c>NotificationConfiguration</c>), so ordering by the column itself sorts alphabetically —
    /// Critical, High, <b>Low</b>, Medium — which floats Low above Medium. Projecting the enum back
    /// onto its ordinal makes the database order by urgency; EF translates this to a CASE
    /// expression, so paging still happens server-side.
    /// </summary>
    private static readonly Expression<Func<Notification, int>> ByUrgency = n =>
        n.Priority == NotificationPriority.Critical ? 1
        : n.Priority == NotificationPriority.High ? 2
        : n.Priority == NotificationPriority.Medium ? 3
        : 4;

    public NotificationRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<Notification>> GetForReconciliationAsync(
        Guid userId, CancellationToken ct = default)
        => await _dbSet
            .Where(n => n.UserId == userId && n.IsActive)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Notification>> QueryAsync(
        Guid userId,
        NotificationState? state,
        NotificationCategory? category,
        Guid? cardiMemberId,
        bool? owned,
        int limit,
        int offset,
        CancellationToken ct = default)
        => await Filtered(userId, state, category, cardiMemberId, owned)
            .AsNoTracking()
            .OrderBy(ByUrgency)
            .ThenByDescending(n => n.FirstDetectedDate)
            // Ties are real — one run can create several rows in the same instant, and an unstable
            // sort would let paging repeat or skip them.
            .ThenByDescending(n => n.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<int> CountAsync(
        Guid userId,
        NotificationState? state,
        NotificationCategory? category,
        Guid? cardiMemberId,
        bool? owned,
        CancellationToken ct = default)
        => await Filtered(userId, state, category, cardiMemberId, owned).CountAsync(ct);

    public async Task<int> CountUnseenAsync(Guid userId, CancellationToken ct = default)
        => await _dbSet.CountAsync(
            n => n.UserId == userId
                 && n.IsActive
                 && n.IsOwner
                 && n.State == NotificationState.Open
                 && n.FirstSeenDate == null,
            ct);

    public async Task<IReadOnlyList<Notification>> GetTopForDashboardAsync(
        Guid userId, int limit, DateTime utcNow, CancellationToken ct = default)
        => await _dbSet
            .AsNoTracking()
            .Where(n => n.UserId == userId
                        && n.IsActive
                        && n.IsOwner
                        // Mirrors Notification.IsVisible: an expired snooze is showable again even
                        // before the next run flips the stored state back to Open.
                        && (n.State == NotificationState.Open
                            || (n.State == NotificationState.Snoozed && n.SnoozedUntil <= utcNow)))
            .OrderBy(ByUrgency)
            .ThenByDescending(n => n.FirstDetectedDate)
            .ThenByDescending(n => n.Id)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Notification>> GetLiveForCardiMemberAsync(
        Guid cardiMemberId, CancellationToken ct = default)
        => await _dbSet
            .Where(n => n.CardiMemberId == cardiMemberId
                        && n.IsActive
                        && (n.State == NotificationState.Open || n.State == NotificationState.Snoozed))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Notification>> GetPendingPushAsync(
        IReadOnlyList<string> ruleCodes, int limit, CancellationToken ct = default)
    {
        if (ruleCodes.Count == 0)
            return [];

        // AsNoTracking: the caller does not write through these instances — it claims each row with
        // TryClaimForPushAsync, a conditional UPDATE in its own scope — so tracking them here would
        // hold a graph the sweep never saves.
        return await _dbSet
            .AsNoTracking()
            .Where(n => n.IsActive
                        && n.IsOwner
                        && n.State == NotificationState.Open
                        && n.PushedDate == null
                        && ruleCodes.Contains(n.RuleCode))
            // Oldest gap first: if a backlog ever forms, the warning that has been waiting longest
            // is the one to clear, and a stable order keeps the batch boundary deterministic.
            .OrderBy(n => n.FirstDetectedDate)
            .ThenBy(n => n.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<bool> TryClaimForPushAsync(
        Guid notificationId, DateTime utcNow, CancellationToken ct = default)
    {
        // ExecuteUpdate, so the read and the write are one statement the database arbitrates —
        // see the interface remarks for why an in-memory decision cannot be made safe here.
        // Postgres reports how many rows it actually moved, and that number is the answer.
        var claimed = await _dbSet
            .Where(n => n.Id == notificationId
                        && n.PushedDate == null
                        && n.IsActive
                        && n.IsOwner
                        && n.State == NotificationState.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.PushedDate, utcNow), ct);

        return claimed == 1;
    }

    public async Task ReleasePushClaimAsync(Guid notificationId, CancellationToken ct = default)
        => await _dbSet
            .Where(n => n.Id == notificationId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.PushedDate, (DateTime?)null), ct);

    private IQueryable<Notification> Filtered(
        Guid userId,
        NotificationState? state,
        NotificationCategory? category,
        Guid? cardiMemberId,
        bool? owned)
    {
        var query = _dbSet.Where(n => n.UserId == userId && n.IsActive);

        if (state.HasValue)
            query = query.Where(n => n.State == state.Value);

        if (category.HasValue)
            query = query.Where(n => n.Category == category.Value);

        if (cardiMemberId.HasValue)
            query = query.Where(n => n.CardiMemberId == cardiMemberId.Value);

        if (owned.HasValue)
            query = query.Where(n => n.IsOwner == owned.Value);

        return query;
    }
}
