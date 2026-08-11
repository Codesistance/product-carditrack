using System.Diagnostics;
using System.Diagnostics.Metrics;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.Infrastructure.Diagnostics;

/// <summary>
/// Instruments for the push delivery spine — send calls, the dispatch worker's batch loop,
/// escalation transitions (notification_engine.md §13). One name
/// (<see cref="TelemetryNames.PushSource"/>) covers both the ActivitySource and the Meter, same
/// shape as <see cref="AiTelemetry"/>.
///
/// Privacy invariant (§7.1): nothing recorded here may ever carry a notification's title, body,
/// or any field that could hold PHI. Delivery ids, categories, states, and provider status codes
/// only.
/// </summary>
public static class PushTelemetry
{
    public static readonly ActivitySource Source = new(TelemetryNames.PushSource);
    public static readonly Meter Meter = new(TelemetryNames.PushSource);

    public static readonly Counter<long> Enqueued = Meter.CreateCounter<long>(
        "notification.enqueued", description: "Deliveries written to the outbox, by category");

    public static readonly Counter<long> Sent = Meter.CreateCounter<long>(
        "notification.sent", description: "Deliveries accepted by the provider, by category");

    public static readonly Counter<long> Delivered = Meter.CreateCounter<long>(
        "notification.delivered", description: "Deliveries acknowledged by the client, by category");

    public static readonly Counter<long> Failed = Meter.CreateCounter<long>(
        "notification.failed", description: "Retryable send failures, by category and reason");

    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "notification.dead_lettered", description: "Deliveries that exhausted retries before the message TTL");

    public static readonly Counter<long> Escalated = Meter.CreateCounter<long>(
        "notification.escalated", description: "Escalation ladder transitions, by stage");

    public static readonly Counter<long> UndeliveredCritical = Meter.CreateCounter<long>(
        "notification.undelivered_critical", description: "Escalation exhausted with no ack from anyone — paged operational event");

    /// <summary>The SLO metric (§6.1) — p99 for Safety must stay under 60s. Alerting on the breach is a Datadog monitor, not code.</summary>
    public static readonly Histogram<double> TimeToAck = Meter.CreateHistogram<double>(
        "notification.time_to_ack", unit: "s", description: "Seconds from enqueue to client ack");

    /// <summary>Per-call FCM RPC duration, independent of <see cref="TimeToAck"/> — this measures the send, not the round trip to the device.</summary>
    public static readonly Histogram<double> FcmSendDuration = Meter.CreateHistogram<double>(
        "fcm.send.duration", unit: "s", description: "Duration of a single FCM SendAsync call");

    public static readonly Counter<long> TokenChurn = Meter.CreateCounter<long>(
        "notification.token_churn", description: "Push device tokens registered or disabled, by reason");

    public const string CategoryTag = "notification.category";
    public const string ReasonTag = "notification.reason";
    public const string StageTag = "notification.stage";
    public const string ErrorTypeTag = "error.type";
}
