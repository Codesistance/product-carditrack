namespace CardiTrack.PipelineJobs.Notifications;

/// <summary>One received webhook notification: the raw body the receiver forwarded, plus the
/// handle to acknowledge it with. <paramref name="TraceParent"/> is the W3C traceparent string
/// the publisher stamped on the message (see PubSubNotificationPublisher), null when it wasn't
/// set — e.g. no APM engine configured when the notification was published.</summary>
public sealed record ReceivedNotification(string AckId, string Body, string? TraceParent = null);

/// <summary>
/// The thin seam over the realtime pull subscription, so the drain logic is unit-testable
/// without Pub/Sub. Delivery is at-least-once: anything not acknowledged comes back.
/// </summary>
public interface INotificationSource
{
    Task<IReadOnlyList<ReceivedNotification>> PullAsync(int maxMessages, CancellationToken ct);

    Task AcknowledgeAsync(IReadOnlyList<string> ackIds, CancellationToken ct);
}
