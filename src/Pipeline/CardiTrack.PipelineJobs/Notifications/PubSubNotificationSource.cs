using Google.Cloud.PubSub.V1;

namespace CardiTrack.PipelineJobs.Notifications;

/// <summary>Pull-subscription implementation over the low-level subscriber API — explicit pull
/// and acknowledge, the shape a run-to-completion job wants (the streaming client is for
/// long-lived listeners).</summary>
public sealed class PubSubNotificationSource : INotificationSource
{
    private readonly SubscriberServiceApiClient _client;
    private readonly SubscriptionName _subscription;

    public PubSubNotificationSource(SubscriberServiceApiClient client, SubscriptionName subscription)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(subscription);
        _client = client;
        _subscription = subscription;
    }

    public async Task<IReadOnlyList<ReceivedNotification>> PullAsync(int maxMessages, CancellationToken ct)
    {
        var response = await _client.PullAsync(_subscription, maxMessages, ct);
        return response.ReceivedMessages
            .Select(m => new ReceivedNotification(m.AckId, m.Message.Data.ToStringUtf8()))
            .ToList();
    }

    public async Task AcknowledgeAsync(IReadOnlyList<string> ackIds, CancellationToken ct)
    {
        if (ackIds.Count == 0)
            return;

        await _client.AcknowledgeAsync(_subscription, ackIds, ct);
    }
}
