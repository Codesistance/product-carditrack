using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
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
    private readonly IServiceProvider _provider = Substitute.For<IServiceProvider>();

    public NotificationDispatchWorkerTests()
    {
        _unitOfWork.NotificationDeliveries.Returns(_deliveries);
        _unitOfWork.PushDeviceTokens.Returns(_tokens);

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
        _provider.GetService(typeof(IDispatchService)).Returns(Substitute.For<IDispatchService>());
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
