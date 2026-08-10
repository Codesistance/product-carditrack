using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface INotificationRepository : IRepository<Notification>
{
    /// <summary>
    /// Every stored row for a user regardless of state — reconciliation needs the resolved and
    /// snoozed ones too, or it would recreate a gap the user has already snoozed.
    /// </summary>
    Task<IReadOnlyList<Notification>> GetForReconciliationAsync(Guid userId, CancellationToken ct = default);

    /// <summary>One page of a user's inbox, priority-ranked.</summary>
    Task<IReadOnlyList<Notification>> QueryAsync(
        Guid userId,
        NotificationState? state,
        NotificationCategory? category,
        Guid? cardiMemberId,
        bool? owned,
        int limit,
        int offset,
        CancellationToken ct = default);

    Task<int> CountAsync(
        Guid userId,
        NotificationState? state,
        NotificationCategory? category,
        Guid? cardiMemberId,
        bool? owned,
        CancellationToken ct = default);

    /// <summary>
    /// Open, owned rows the user has not yet seen — the inbox badge. Deliberately unaffected by
    /// the caller's filters, the same way the alert badge is.
    /// </summary>
    Task<int> CountUnseenAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The top <paramref name="limit"/> visible owned rows for the dashboard card slots.</summary>
    Task<IReadOnlyList<Notification>> GetTopForDashboardAsync(
        Guid userId, int limit, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Open or snoozed rows scoped to one CardiMember, across every user — used when a member is
    /// paused or removed and their notifications must all be withdrawn at once.
    /// </summary>
    Task<IReadOnlyList<Notification>> GetLiveForCardiMemberAsync(
        Guid cardiMemberId, CancellationToken ct = default);
}
