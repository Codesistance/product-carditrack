using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

/// <inheritdoc cref="INotificationGapResolver"/>
public class NotificationGapResolver : INotificationGapResolver
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly INotificationSnapshotQueries _snapshots;
    private readonly TimeProvider _timeProvider;

    public NotificationGapResolver(
        IUnitOfWork unitOfWork,
        INotificationSnapshotQueries snapshots,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _snapshots = snapshots;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task ResolveForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var contexts = await _snapshots.BuildContextsForUserAsync(userId, UtcNow, ct);
        await ApplyAsync(userId, contexts, ct);
    }

    public async Task ResolveForCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default)
    {
        var contexts = await _snapshots.BuildContextsForCardiMemberAsync(cardiMemberId, UtcNow, ct);

        // One member can be watched by several caregivers, and reconciliation is per user — each
        // owner's stored set has to be diffed against their own contexts.
        foreach (var group in contexts.GroupBy(c => c.User.Id))
            await ApplyAsync(group.Key, [.. group], ct);
    }

    public async Task WithdrawForCardiMemberAsync(
        Guid cardiMemberId, NotificationResolutionReason reason, CancellationToken ct = default)
    {
        var live = await _unitOfWork.Notifications.GetLiveForCardiMemberAsync(cardiMemberId, ct);
        if (live.Count == 0)
            return;

        var now = UtcNow;
        foreach (var notification in live)
        {
            // Superseded, not Resolved: the gap was not closed, it stopped applying. Counting a
            // pause as a success would flatter the comply rate the review gate depends on.
            notification.State = NotificationState.Superseded;
            notification.ResolutionReason = reason;
            notification.ResolvedDate = now;
            notification.LastEvaluatedDate = now;
            _unitOfWork.Notifications.Update(notification);
        }

        await _unitOfWork.SaveChangesAsync();
    }

    private async Task ApplyAsync(Guid userId, IReadOnlyList<NudgeContext> contexts, CancellationToken ct)
    {
        if (contexts.Count == 0)
            return;

        var stored = await _unitOfWork.Notifications.GetForReconciliationAsync(userId, ct);
        var plan = NudgeReconciler.Reconcile(contexts, stored);

        if (plan.ToInsert.Count == 0 && plan.ToUpdate.Count == 0)
            return;

        if (plan.ToInsert.Count > 0)
            await _unitOfWork.Notifications.AddRangeAsync(plan.ToInsert);

        foreach (var notification in plan.ToUpdate)
            _unitOfWork.Notifications.Update(notification);

        await _unitOfWork.SaveChangesAsync();
    }
}
