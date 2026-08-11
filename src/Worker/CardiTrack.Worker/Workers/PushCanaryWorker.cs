using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Diagnostics;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

public class PushCanaryOptions
{
    /// <summary>User ids of the fleet of test devices the canary sends to. Configured, not
    /// auto-discovered — a canary that silently picks up real users' devices would page ops for
    /// content nobody signed up to receive.</summary>
    public List<Guid> CanaryUserIds { get; set; } = [];
}

/// <summary>
/// Sends a real Safety-category push to a fleet of test devices every 15 minutes and checks
/// whether the <em>previous</em> run's canary got acked (notification_engine.md §13). Provider
/// outages, expired credentials and a broken APNs certificate are otherwise silent failures — the
/// kind discovered from a support ticket during an actual emergency.
/// </summary>
/// <remarks>
/// Deliberately routed through the normal outbox (<see cref="IDispatchService.EnqueueAsync"/>)
/// rather than a bespoke send path — the canary is only meaningful if it exercises the exact same
/// planning, send, and ack code every real Safety alert goes through. A canary with its own
/// shortcut path could stay green while the real path breaks.
/// </remarks>
public class PushCanaryWorker : CronBackgroundService
{
    /// <summary>Matches the escalation ladder's own SLO window (§6.1) — a canary that hasn't acked by the time the next one fires has already missed the target that matters.</summary>
    private static readonly TimeSpan AckWindow = TimeSpan.FromMinutes(15);

    private const string DedupPrefix = "canary";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly List<Guid> _canaryUserIds;
    private readonly ILogger<PushCanaryWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public PushCanaryWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<PushCanaryOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<PushCanaryWorker> logger,
        TimeProvider? timeProvider = null)
        : base(workerOptions.Get(nameof(PushCanaryWorker)).CronExpression)
    {
        _scopeFactory = scopeFactory;
        _canaryUserIds = options.CurrentValue.CanaryUserIds;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        if (_canaryUserIds.Count == 0)
        {
            // Nothing configured yet (Workers:PushCanaryWorker:CanaryUserIds) — skip quietly
            // rather than paging on an empty fleet nobody set up.
            return;
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

        await CheckPreviousRunAsync(unitOfWork, utcNow, stoppingToken);

        foreach (var userId in _canaryUserIds)
        {
            await dispatch.EnqueueAsync(new EnqueueRequest(
                SourceType: DeliverySourceType.Notification,
                SourceId: Guid.Empty,
                UserId: userId,
                CardiMemberId: null,
                Category: DeliveryCategory.Safety,
                Severity: null,
                DedupKey: $"{DedupPrefix}:{userId}:{utcNow:yyyyMMddHHmm}",
                CollapseKey: $"{DedupPrefix}-{userId}"), stoppingToken);
        }
    }

    private async Task CheckPreviousRunAsync(IUnitOfWork unitOfWork, DateTime utcNow, CancellationToken ct)
    {
        foreach (var userId in _canaryUserIds)
        {
            var previousBucket = utcNow - AckWindow;
            var dedupKey = $"{DedupPrefix}:{userId}:{previousBucket:yyyyMMddHHmm}";

            var previous = await unitOfWork.NotificationDeliveries.GetByDedupKeyAsync(dedupKey, ct);
            if (previous is null)
                continue;

            if (previous.State != DeliveryState.Delivered)
            {
                PushTelemetry.UndeliveredCritical.Add(1,
                    new KeyValuePair<string, object?>(PushTelemetry.ReasonTag, "canary"));
                _logger.LogCritical(
                    "Push canary for user {UserId} did not ack within {Window} — provider outage, " +
                    "expired credential, or broken APNs cert. Delivery {DeliveryId}, state {State}.",
                    userId, AckWindow, previous.Id, previous.State);
            }
        }
    }
}
