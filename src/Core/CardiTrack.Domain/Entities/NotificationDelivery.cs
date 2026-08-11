using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// The transactional outbox both <see cref="Alert"/> and <see cref="Notification"/> write to —
/// the shared reliability substrate that makes retry, escalation and SLO measurement work
/// uniformly across two domain models that otherwise have nothing in common
/// (notification_engine.md §6, §8).
/// </summary>
/// <remarks>
/// Claimed by <c>NotificationDispatchWorker</c> via <c>FOR UPDATE SKIP LOCKED</c>, not an
/// advisory lock — three Cloud Run instances need to divide the outbox in parallel every 30
/// seconds, not take turns running the whole batch (§13).
/// </remarks>
public class NotificationDelivery : BaseEntity
{
    public DeliverySourceType SourceType { get; set; }

    /// <summary>Polymorphic FK — <see cref="Alert"/>.Id or <see cref="Notification"/>.Id, per <see cref="SourceType"/>.</summary>
    public Guid SourceId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// Denormalised from the source row at enqueue time. <see cref="SourceId"/> is polymorphic and
    /// cannot be joined generically, so without this the erasure sweep has nothing to filter on.
    /// </summary>
    public Guid? CardiMemberId { get; set; }

    public DeliveryCategory Category { get; set; }

    /// <summary>
    /// Set only for <see cref="DeliveryCategory.Health"/> (sourced from an <see cref="Alert"/>) —
    /// null for Safety and Nudge. Carried on the row, not re-derived, so send-time code
    /// (<c>FcmNotificationChannel</c>'s critical-flag/interruption-level decision, escalation's
    /// red-only rule) sees exactly what <c>DeliveryPlanner</c> planned against, not a guess.
    /// </summary>
    public AlertSeverity? Severity { get; set; }

    public DeliveryChannel Channel { get; set; }
    public DeliveryState State { get; set; } = DeliveryState.Pending;

    public Guid? PushDeviceTokenId { get; set; }

    /// <summary>
    /// Namespaced by producer — <c>worker:device-silence:{connectionId}:{utcDate}</c>,
    /// <c>pipeline:device-silence:…</c>. Cross-producer suppression is an explicit collapse rule
    /// evaluated at send time (<c>DeliveryPlanner</c>), never a side effect of this unique index —
    /// a shared namespace would let one producer silently pre-claim and drop another's alert.
    /// </summary>
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>FCM <c>collapse_key</c> / APNs <c>apns-collapse-id</c> — the OS keeps only the newest.</summary>
    public string? CollapseKey { get; set; }

    /// <summary>Message TTL. A red alert undelivered this long is stale — let it expire rather than surprise someone later.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Quiet-hours deferral lands here. Null means "send now".</summary>
    public DateTime? ScheduledFor { get; set; }

    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? ProviderMessageId { get; set; }

    public DateTime? SentDate { get; set; }

    /// <summary>Client ack via <c>POST /delivered</c> — proves the pipe worked, distinct from <c>Notification.FirstSeenDate</c> proving the human engaged.</summary>
    public DateTime? DeliveredDate { get; set; }

    public EscalationStage EscalationStage { get; set; } = EscalationStage.Initial;

    /// <summary>Set on a re-push/fan-out row, pointing back at the delivery it escalated from.</summary>
    public Guid? EscalatedFrom { get; set; }
}
