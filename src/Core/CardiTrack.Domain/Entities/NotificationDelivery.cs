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

    /// <summary>
    /// Set only for <see cref="DeliveryCategory.Health"/>. Lets the push teaser name the kind of
    /// alert (heart / sleep / activity) without loading the <see cref="Alert"/> row — and without
    /// putting its message (which can carry metric numbers) on the lock screen.
    /// </summary>
    public AlertType? AlertType { get; set; }

    /// <summary>
    /// The nudge rule behind a <see cref="DeliverySourceType.Notification"/> push (for example
    /// <c>DEVICE_AUTH_BROKEN</c>), null for every other source. The <see cref="AlertType"/> of the
    /// nudge side: it lets the push teaser say what happened ("Device needs reconnecting") without
    /// loading the <see cref="Notification"/> row at send time, and a rule code carries no name or
    /// reading, so it is as safe on the lock screen as the alert kind is.
    /// </summary>
    public string? NudgeRuleCode { get; set; }

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
    /// <summary>
    /// Why the row is where it is, when that needs saying — a send failure, or the reason a
    /// <see cref="DeliveryState.Suppressed"/> row was never attempted. Named for the common case;
    /// not every value in it is an error.
    /// </summary>
    public string? LastError { get; set; }
    public string? ProviderMessageId { get; set; }

    public DateTime? SentDate { get; set; }

    /// <summary>Client ack via <c>POST /delivered</c> — proves the pipe worked, distinct from <c>Notification.FirstSeenDate</c> proving the human engaged.</summary>
    public DateTime? DeliveredDate { get; set; }

    /// <summary>
    /// The W3C traceparent (<c>00-&lt;trace-id&gt;-&lt;span-id&gt;-01</c>) of the most recent send
    /// attempt's Activity — lets the eventual ack, which can arrive independently up to the
    /// escalation ladder's 900s ceiling later, link back to the trace that sent it. Overwritten by
    /// every repush, so this reflects only the latest attempt, not a full history of each one.
    /// </summary>
    public string? SendTraceParent { get; set; }

    public EscalationStage EscalationStage { get; set; } = EscalationStage.Initial;

    /// <summary>
    /// Whether this row is itself an escalated copy — written by the ladder's fan-out rung to a
    /// caregiver who is not the alert's original recipient.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Persisted rather than derived, for one reason: the ladder must not fan a copy out again.
    /// A copy is an ordinary red Health row in every other respect, so the escalation sweep would
    /// otherwise reach its own t+300s rung, copy it to everybody else — the original recipient
    /// included — and repeat. A family of four turns three pushes into nine and then
    /// twenty-seven, which is precisely the alarm fatigue this engine exists to prevent.
    /// </para>
    /// <para>
    /// It does not stop the ladder entirely: a copy nobody answers still re-pushes at t+120s and
    /// still reaches <c>UNDELIVERED_CRITICAL</c> at t+900s, because a family with no cover has to
    /// find that out. Only the rung that would widen the blast radius is spent.
    /// </para>
    /// </remarks>
    public bool IsEscalation { get; set; }

    /// <summary>Set on a re-push/fan-out row, pointing back at the delivery it escalated from.</summary>
    public Guid? EscalatedFrom { get; set; }
}
