using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Worker;
using CardiTrack.Worker.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// Pins the blast radius of a failing dispatch phase. The Worker does not override
/// <c>BackgroundServiceExceptionBehavior</c>, so anything escaping a tick stops the whole host —
/// which is exactly what a misconfigured <c>FirebaseApp</c> did on 2026-08-12: resolving
/// <c>IDispatchService</c> threw on every 30-second sweep, and because it threw at *resolution* it
/// escaped the per-row try/catch, taking wearable sync and partition maintenance down with it for
/// 224 restarts.
/// </summary>
public class NotificationDispatchWorkerTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly INotificationDeliveryRepository _deliveries = Substitute.For<INotificationDeliveryRepository>();
    private readonly IPushDeviceTokenRepository _tokens = Substitute.For<IPushDeviceTokenRepository>();
    private readonly INotificationRepository _notifications = Substitute.For<INotificationRepository>();
    private readonly IDispatchService _dispatch = Substitute.For<IDispatchService>();
    private readonly IServiceProvider _provider = Substitute.For<IServiceProvider>();

    public NotificationDispatchWorkerTests()
    {
        _unitOfWork.NotificationDeliveries.Returns(_deliveries);
        _unitOfWork.PushDeviceTokens.Returns(_tokens);
        _unitOfWork.Notifications.Returns(_notifications);

        _notifications.GetPendingPushAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Notification>());

        // Nothing due anywhere: every phase reaches its own work but finds an empty queue, so the
        // only thing under test is what happens when one of them throws.
        _deliveries.ClaimDueAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<NotificationDelivery>());
        _deliveries.GetDueForEscalationAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<NotificationDelivery>());
        _deliveries.GetExpiredAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<NotificationDelivery>());
        _tokens.GetDueForLivenessProbeAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PushDeviceToken>());

        _provider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
        _provider.GetService(typeof(IDispatchService)).Returns(_dispatch);
    }

    [Fact]
    public async Task ExecuteJob_DoesNotPropagate_WhenResolvingTheDispatchServiceThrows()
    {
        // The 2026-08-12 shape exactly: the push stack fails to construct, not a row failing to send.
        _provider.GetService(typeof(IDispatchService))
            .Throws(new InvalidOperationException("Value cannot be null. (Parameter 'Credential must be set')"));

        var worker = CreateWorker();

        var thrown = await Record.ExceptionAsync(() => worker.RunOnceAsync(CancellationToken.None));

        Assert.Null(thrown);
    }

    [Fact]
    public async Task ExecuteJob_StillRunsLaterPhases_WhenTheEscalationSweepThrows()
    {
        _provider.GetService(typeof(IDispatchService))
            .Throws(new InvalidOperationException("push stack unavailable"));

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        // Expiry and the token-staleness sweep touch no push infrastructure, so a dead push stack
        // must not stop rows ageing out of the outbox.
        await _deliveries.Received(1).GetExpiredAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _tokens.Received(1).GetDueForLivenessProbeAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteJob_PropagatesCancellation_SoShutdownIsNotSwallowed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _deliveries.ClaimDueAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateWorker().RunOnceAsync(cancelled.Token));
    }

    // ── The Safety-nudge push sweep ───────────────────────────────────────────
    //
    // A Safety-class nudge is documented as delivering harder than a red Alert (§15), and until
    // this sweep existed it delivered to the inbox and nowhere else — DataCompletenessWorker wrote
    // the row and no code turned it into a NotificationDelivery.

    [Fact]
    public async Task PushSweep_EnqueuesASafetyDelivery_ForAnOpenPushableNudge()
    {
        var notification = PendingNudge();
        StagePending(notification);

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        await _dispatch.Received(1).EnqueueAsync(
            Arg.Is<EnqueueRequest>(r =>
                r.SourceType == DeliverySourceType.Notification
                && r.SourceId == notification.Id
                && r.UserId == notification.UserId
                && r.CardiMemberId == notification.CardiMemberId
                // Safety, so DeliveryPlanner gives it the immediate send, the quiet-hours override
                // and the escalation ladder — the point of routing a nudge here at all.
                && r.Category == DeliveryCategory.Safety
                && r.Severity == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushSweep_StampsPushedDate_SoTheNextTickDoesNotSendItAgain()
    {
        var notification = PendingNudge();
        StagePending(notification);

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        Assert.NotNull(notification.PushedDate);
        _notifications.Received(1).Update(notification);
        await _unitOfWork.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task PushSweep_LeavesPushedDateUnset_WhenTheEnqueueFails()
    {
        // Every row in the batch shares one DbContext. A PushedDate left set on a row whose enqueue
        // threw would be committed by the *next* row's save, marking a warning as delivered that
        // was never sent — and the sweep would never look at it again.
        var notification = PendingNudge();
        StagePending(notification);
        _dispatch.EnqueueAsync(Arg.Any<EnqueueRequest>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("push stack unavailable"));

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        Assert.Null(notification.PushedDate);
    }

    [Fact]
    public async Task PushSweep_AsksOnlyForRulesThatDeclareTheyPush()
    {
        await CreateWorker().RunOnceAsync(CancellationToken.None);

        await _notifications.Received(1).GetPendingPushAsync(
            Arg.Is<IReadOnlyList<string>>(codes =>
                codes.Contains("DEVICE_BATTERY_LOW")
                && codes.Contains("DEVICE_AUTH_BROKEN")
                // Safety class, and must never push: a notification saying push is broken,
                // delivered by push, is either a contradiction or proof it has already closed.
                && !codes.Contains("PUSH_UNREACHABLE")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushSweep_DedupKeyIsPerArming_AndCollapseKeyIsPerGap()
    {
        var notification = PendingNudge();
        StagePending(notification);

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        await _dispatch.Received(1).EnqueueAsync(
            Arg.Is<EnqueueRequest>(r =>
                // Per arming — a delivery's dedup key is matched in any state and never expires,
                // so a key of the fingerprint alone would push the first flat battery and swallow
                // every one after it.
                r.DedupKey.StartsWith($"nudge:{notification.Fingerprint}:")
                && r.DedupKey != $"nudge:{notification.Fingerprint}:"
                // Per gap — on the device, the second warning about one battery replaces the first.
                && r.CollapseKey == $"nudge-{notification.Fingerprint}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushSweep_FailureDoesNotStopTheRestOfTheTick()
    {
        _notifications.GetPendingPushAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("notification table unreachable"));

        var thrown = await Record.ExceptionAsync(() => CreateWorker().RunOnceAsync(CancellationToken.None));

        Assert.Null(thrown);
        await _deliveries.Received(1).GetExpiredAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    private static Notification PendingNudge() => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        CardiMemberId = Guid.NewGuid(),
        RuleCode = "DEVICE_BATTERY_LOW",
        Fingerprint = "fingerprint-abc",
        State = NotificationState.Open
    };

    private void StagePending(params Notification[] notifications) =>
        _notifications.GetPendingPushAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(notifications);

    private TestableDispatchWorker CreateWorker()
    {
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(_provider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var options = Substitute.For<IOptionsMonitor<WorkerOptions>>();
        options.Get(nameof(NotificationDispatchWorker))
            .Returns(new WorkerOptions { CronExpression = "*/30 * * * * *" });

        return new TestableDispatchWorker(options, scopeFactory, NullLogger<NotificationDispatchWorker>.Instance);
    }

    /// <summary>Exposes the protected tick, so the test drives one sweep rather than the cron loop.</summary>
    private sealed class TestableDispatchWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        Microsoft.Extensions.Logging.ILogger<NotificationDispatchWorker> logger)
        : NotificationDispatchWorker(options, scopeFactory, logger)
    {
        public Task RunOnceAsync(CancellationToken ct) => ExecuteJobAsync(ct);
    }
}
