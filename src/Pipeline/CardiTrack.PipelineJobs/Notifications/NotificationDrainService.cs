using System.Diagnostics;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Extensions;

namespace CardiTrack.PipelineJobs.Notifications;

public sealed record NotificationDrainSummary(
    int Messages, int SyncedConnections, int UnknownUsers, int Unparseable, int FailedUsers);

public interface INotificationDrainService
{
    Task<NotificationDrainSummary> DrainAsync(CancellationToken ct = default);
}

/// <summary>
/// The aggregation half of the real-time path (docs/llm_design.md `WearableAggregator`), first
/// increment: drain the realtime subscription, map each notification's health-user id to its
/// connections, and run a targeted sync — the same `SyncCardiMemberAsync` the Worker's cadence
/// runs, just sooner. Reusing the sync wholesale is the point: every invariant (success
/// stamping, granular ingestion, backfill resume, pause exclusion) comes along for free, and
/// stamping `LastSyncDate` naturally pushes the routine poll out, making polling the fallback
/// rather than a duplicate. The SSA pre-processing and MedGemma assessment stack on top in
/// later slices.
/// </summary>
public sealed class NotificationDrainService : INotificationDrainService
{
    private const int PullBatchSize = 100;

    /// <summary>Upper bound on one run's batches — a runaway backlog is drained across several
    /// 5-minute executions rather than one unbounded one.</summary>
    private const int MaxBatches = 50;

    private readonly INotificationSource _source;
    private readonly IDeviceConnectionRepository _connections;
    private readonly IServiceProvider _services;
    private readonly ILogger<NotificationDrainService> _logger;

    public NotificationDrainService(
        INotificationSource source,
        IDeviceConnectionRepository connections,
        IServiceProvider services,
        ILogger<NotificationDrainService> logger)
    {
        _source = source;
        _connections = connections;
        _services = services;
        _logger = logger;
    }

    public async Task<NotificationDrainSummary> DrainAsync(CancellationToken ct = default)
    {
        var messages = 0;
        var unparseable = 0;
        var unknownUsers = new HashSet<string>(StringComparer.Ordinal);
        var failedUsers = new HashSet<string>(StringComparer.Ordinal);
        var syncedConnections = new HashSet<Guid>();

        for (var batch = 0; batch < MaxBatches && !ct.IsCancellationRequested; batch++)
        {
            var received = await _source.PullAsync(PullBatchSize, ct);
            if (received.Count == 0)
                break;

            messages += received.Count;
            var ackable = new List<string>();

            // Parse first, sync per distinct user id — one notification burst for a user must
            // not become one sync per message.
            var idsByMessage = new Dictionary<ReceivedNotification, IReadOnlyCollection<string>>();
            foreach (var message in received)
            {
                // Linked (not parented) to the publish span: one job execution fans many
                // publishers' messages into per-user syncs, so there is no single parent to
                // attach to. This is what makes the webhook-receiver -> pipeline-jobs hop
                // visible on Datadog's Service Map.
                using var activity = PipelineTelemetry.Source.StartActivity(
                    "ProcessNotification", ActivityKind.Consumer, default(ActivityContext),
                    links: PipelineTelemetry.BuildLinks(message.TraceParent));

                var ids = WebhookNotificationParser.ExtractHealthUserIds(message.Body);
                if (ids.Count == 0)
                {
                    // Poison-tolerant: an unreadable notification is acknowledged, because the
                    // routine poll guarantees the data is not lost — only the shape (names,
                    // never values) is logged, to pin the real schema on first live traffic.
                    unparseable++;
                    _logger.LogWarning(
                        "Webhook notification carried no user resource name; top-level shape: {Shape}.",
                        WebhookNotificationParser.TopLevelShape(message.Body));
                    ackable.Add(message.AckId);
                    continue;
                }

                idsByMessage[message] = ids;
            }

            foreach (var healthUserId in idsByMessage.Values.SelectMany(ids => ids).Distinct())
            {
                if (failedUsers.Contains(healthUserId) || unknownUsers.Contains(healthUserId))
                    continue;

                var connections = (await _connections.GetSyncableByHealthUserIdAsync(healthUserId)).ToList();
                if (connections.Count == 0)
                {
                    // A wearer who disconnected (or paused) mid-flight is a fact, not an error —
                    // and the paused/removed exclusion in the lookup must win over the webhook.
                    unknownUsers.Add(healthUserId);
                    continue;
                }

                foreach (var connection in connections.Where(c => !syncedConnections.Contains(c.Id)))
                {
                    try
                    {
                        var syncService = _services.GetDeviceSyncService(connection.DeviceType);
                        if (syncService is null)
                        {
                            // A registration gap is a deploy/config error, not a wearer fact —
                            // hold the messages for redelivery so the retry path survives until
                            // the fix ships, unlike an unknown user where there is nothing to
                            // ever retry.
                            failedUsers.Add(healthUserId);
                            _logger.LogError(
                                "No sync service registered for DeviceType {DeviceType}; holding notifications for DeviceConnection {Id}.",
                                connection.DeviceType, connection.Id);
                            continue;
                        }

                        await syncService.SyncCardiMemberAsync(connection, SyncScope.WorkerCadence);
                        syncedConnections.Add(connection.Id);
                    }
                    catch (Exception ex)
                    {
                        // Leave this user's messages unacknowledged: redelivery retries them on
                        // the next run, and the sync is idempotent end to end.
                        failedUsers.Add(healthUserId);
                        _logger.LogError(ex,
                            "Notification-triggered sync failed for DeviceConnection {Id} (DeviceType={DeviceType}).",
                            connection.Id, connection.DeviceType);
                    }
                }
            }

            // A message is acknowledged when nothing in it still needs a retry: every id either
            // synced, or is unknown (nothing to do, ever).
            ackable.AddRange(idsByMessage
                .Where(pair => pair.Value.All(id => !failedUsers.Contains(id)))
                .Select(pair => pair.Key.AckId));

            await _source.AcknowledgeAsync(ackable, ct);
        }

        return new NotificationDrainSummary(
            messages, syncedConnections.Count, unknownUsers.Count, unparseable, failedUsers.Count);
    }
}
