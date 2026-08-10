using System.Diagnostics;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;

namespace CardiTrack.HealthWebhookReceiver;

/// <summary>
/// Publishes the raw notification to the realtime topic. The body travels as the message data,
/// untouched; receive metadata rides as attributes so the aggregator can reason about staleness
/// without parsing anything.
/// </summary>
public sealed class PubSubNotificationPublisher : INotificationPublisher
{
    private readonly PublisherClient _client;

    public PubSubNotificationPublisher(PublisherClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task PublishAsync(
        string body, string? contentType, DateTime receivedAtUtc, CancellationToken ct)
    {
        var message = new PubsubMessage
        {
            Data = ByteString.CopyFromUtf8(body),
            Attributes =
            {
                ["receivedAtUtc"] = receivedAtUtc.ToString("O"),
                ["contentType"] = contentType ?? string.Empty,
            },
        };

        // Carries the inbound request's trace context across the async hop so pipeline-jobs can
        // link its processing span back to this publish — otherwise the webhook-receiver →
        // pipeline-jobs edge is invisible to Datadog's Service Map. Activity.Id is the W3C
        // traceparent string under the SDK's default ActivityIdFormat.W3C. Absent when this
        // runs with no ambient Activity (e.g. no APM engine configured) — the aggregator side
        // degrades to today's unlinked behavior, not an error.
        if (Activity.Current is { } activity)
            message.Attributes["traceparent"] = activity.Id!;

        // PublisherClient owns batching and exposes no cancellable overload, so cancellation
        // stops the WAIT, not the publish: an aborted request answers 5xx and Google retries,
        // while the in-flight publish may still land. That is at-least-once delivery doing its
        // job — the aggregator has to be idempotent regardless.
        await _client.PublishAsync(message).WaitAsync(ct);
    }
}
