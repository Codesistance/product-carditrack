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

    /// <summary>
    /// Open, owned rows for the pushable Safety rules that have not been handed to the push outbox
    /// for their current arming — the dispatch worker's enqueue sweep (§6.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Open only, never snoozed.</b> A Safety row can be snoozed for up to 72 hours, and a
    /// snooze the caregiver chose is not a state to push through.
    /// </para>
    /// <para>
    /// <b>Owned only.</b> The read-only copy a second caregiver holds exists so everyone can see
    /// the gap is outstanding, not so five phones ring about one flat battery — the escalation
    /// ladder is what reaches the others, and only when nobody acks.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Notification>> GetPendingPushAsync(
        IReadOnlyList<string> ruleCodes, int limit, CancellationToken ct = default);

    /// <summary>
    /// Claims one row for push by stamping <see cref="Notification.PushedDate"/>, and reports
    /// whether this caller is the one that got it. False means someone else already did, or the
    /// row stopped qualifying between the sweep reading it and reaching it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single conditional UPDATE, not read-then-write. <c>NotificationDispatchWorker</c> runs on
    /// three Cloud Run instances by design and this sweep has no advisory lock or <c>SKIP LOCKED</c>
    /// claim — so all three read the same pending rows. Deciding in memory would send one push per
    /// instance, and the dedup key cannot save it: each tick stamps its own timestamp, so the three
    /// keys differ and <c>DispatchService</c>'s dedup sees three distinct deliveries. Only one
    /// instance's UPDATE can move a row from null, so only one sends.
    /// </para>
    /// <para>
    /// The state predicates ride along for the same reason: a nudge dismissed in the window between
    /// the sweep's read and this call would otherwise still push.
    /// </para>
    /// </remarks>
    Task<bool> TryClaimForPushAsync(Guid notificationId, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Hands a claim back after the enqueue failed, so the next tick can retry it rather than the
    /// warning being marked delivered and never sent.
    /// </summary>
    Task ReleasePushClaimAsync(Guid notificationId, CancellationToken ct = default);
}
