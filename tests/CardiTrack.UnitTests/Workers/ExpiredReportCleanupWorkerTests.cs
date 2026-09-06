using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Worker;
using CardiTrack.Worker.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Workers;

/// <summary>
/// Pins the retention decision and the sweep's failure posture. This job is the only thing that
/// enforces the download window on an export — a complete identified health record sitting in a
/// bucket — so the properties worth pinning are the ones that make a destructive sweep safe to run
/// unattended: the object dies before the row that names it, dry run touches nothing, one bad
/// object cannot end the run, and an abandoned generation is failed out rather than left pending
/// for a caregiver to poll forever.
/// </summary>
public class ExpiredReportCleanupWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 4, 0, 0, TimeSpan.Zero);

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IReportRepository _reports = Substitute.For<IReportRepository>();
    private readonly IReportStorage _storage = Substitute.For<IReportStorage>();
    private readonly IServiceProvider _provider = Substitute.For<IServiceProvider>();
    private readonly ReportStorageOptions _storageOptions = new();

    public ExpiredReportCleanupWorkerTests()
    {
        _unitOfWork.Reports.Returns(_reports);

        // Nothing expired and nothing stale by default — each test stages only what it is about.
        _reports.GetExpiredAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _reports.GetStalePendingAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _provider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
    }

    // ── Expiry ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_DeletesTheObjectAndRemovesTheRow_ForAnExpiredReport()
    {
        var report = ReadyReport(expiresAt: Now.AddDays(-1).UtcDateTime);
        StageExpired(report);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _storage.Received(1).DeleteAsync(report.ObjectName!, Arg.Any<CancellationToken>());
        _reports.Received(1).RemoveRange(Arg.Is<IEnumerable<Report>>(r => r.Contains(report)));
        await _unitOfWork.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task Sweep_DeletesTheObjectBeforeRemovingTheRow()
    {
        // Order matters and only in this direction. Row first would mean a failed blob delete
        // leaves a health record in a bucket with nothing naming it; this way the worst case is a
        // row whose object is already gone, which the next run clears and which reads as expired
        // to the caregiver in the meantime either way.
        var report = ReadyReport(expiresAt: Now.AddDays(-1).UtcDateTime);
        StageExpired(report);

        var deletedBeforeRemoval = false;
        _reports.When(r => r.RemoveRange(Arg.Any<IEnumerable<Report>>()))
            .Do(_ => deletedBeforeRemoval = _storage.ReceivedCalls()
                .Any(c => c.GetMethodInfo().Name == nameof(IReportStorage.DeleteAsync)));

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        Assert.True(deletedBeforeRemoval, "The bucket object must be deleted before the row that names it.");
    }

    [Fact]
    public async Task Sweep_RemovesTheRow_ForAnExpiredReportThatNeverProducedAnObject()
    {
        // A generation that failed before rendering: the row is real, there is nothing to delete.
        var report = ReadyReport(expiresAt: Now.AddDays(-1).UtcDateTime);
        report.Status = ReportStatus.Failed;
        report.ObjectName = null;
        StageExpired(report);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _reports.Received(1).RemoveRange(Arg.Is<IEnumerable<Report>>(r => r.Contains(report)));
    }

    [Fact]
    public async Task Sweep_KeepsTheRow_WhenItsObjectCouldNotBeDeleted()
    {
        // Dropping the row here would strand the object: still expired, no longer named by
        // anything. It is still expired next run, so leaving both is the recoverable state.
        var report = ReadyReport(expiresAt: Now.AddDays(-1).UtcDateTime);
        StageExpired(report);
        _storage.DeleteAsync(report.ObjectName!, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("bucket unavailable"));

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        _reports.DidNotReceive().RemoveRange(Arg.Any<IEnumerable<Report>>());
    }

    [Fact]
    public async Task Sweep_ContinuesPastAnUndeletableObject()
    {
        // One bad object must not cost the sweep the rest of the batch.
        var failing = ReadyReport(expiresAt: Now.AddDays(-2).UtcDateTime);
        var healthy = ReadyReport(expiresAt: Now.AddDays(-1).UtcDateTime);
        StageExpired(failing, healthy);
        _storage.DeleteAsync(failing.ObjectName!, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("bucket unavailable"));

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _storage.Received(1).DeleteAsync(healthy.ObjectName!, Arg.Any<CancellationToken>());
        _reports.Received(1).RemoveRange(
            Arg.Is<IEnumerable<Report>>(r => r.Contains(healthy) && !r.Contains(failing)));
    }

    [Fact]
    public async Task Sweep_DryRun_DeletesNothingAndWritesNothing()
    {
        var expired = ReadyReport(expiresAt: Now.AddDays(-1).UtcDateTime);
        var stale = PendingReport(createdAt: Now.AddHours(-2).UtcDateTime);
        StageExpired(expired);
        StageStale(stale);

        await CreateWorker(dryRun: true).RunSweepAsync(CancellationToken.None);

        await _storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _reports.DidNotReceive().RemoveRange(Arg.Any<IEnumerable<Report>>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
        Assert.Equal(ReportStatus.Pending, stale.Status);
    }

    [Fact]
    public async Task Sweep_AsksForExpiryAsOfNow_NotSomeOtherClock()
    {
        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _reports.Received(1).GetExpiredAsync(
            Now.UtcDateTime, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_BoundsEachPassToTheConfiguredBatchSize()
    {
        await CreateWorker(batchSize: 42).RunSweepAsync(CancellationToken.None);

        await _reports.Received(1).GetExpiredAsync(Arg.Any<DateTime>(), 42, Arg.Any<CancellationToken>());
        await _reports.Received(1).GetStalePendingAsync(Arg.Any<DateTime>(), 42, Arg.Any<CancellationToken>());
    }

    // ── Abandoned generations ───────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_FailsOutAGenerationAbandonedByARestart()
    {
        // The cache-backed design hid these behind its TTL. With durable rows, nothing else ever
        // moves them off Pending — the caregiver would poll a report that will never finish.
        var stale = PendingReport(createdAt: Now.AddHours(-2).UtcDateTime);
        StageStale(stale);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        Assert.Equal(ReportStatus.Failed, stale.Status);
        Assert.Equal(Now.UtcDateTime, stale.CompletedAt);
        _reports.Received(1).Update(stale);
        await _unitOfWork.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task Sweep_GivesAFailedGenerationGenericCopy_NotADiagnostic()
    {
        var stale = PendingReport(createdAt: Now.AddHours(-2).UtcDateTime);
        StageStale(stale);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        // The caregiver may forward this text; the diagnosis belongs in the logs.
        Assert.Equal("Report generation failed. Please try again.", stale.Error);
    }

    [Fact]
    public async Task Sweep_MeasuresAbandonmentFromTheConfiguredGenerationTimeout()
    {
        _storageOptions.GenerationTimeout = TimeSpan.FromMinutes(15);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _reports.Received(1).GetStalePendingAsync(
            Now.AddMinutes(-15).UtcDateTime, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static Report ReadyReport(DateTime expiresAt) => new()
    {
        OwnerUserId = Guid.NewGuid(),
        Format = ReportFormat.Pdf,
        Status = ReportStatus.Ready,
        ObjectName = $"reports/{Guid.NewGuid()}/{Guid.NewGuid():N}.pdf",
        ExpiresAt = expiresAt,
    };

    private static Report PendingReport(DateTime createdAt) => new()
    {
        OwnerUserId = Guid.NewGuid(),
        Format = ReportFormat.Csv,
        Status = ReportStatus.Pending,
        CreatedDate = createdAt,
        ExpiresAt = createdAt.AddDays(7),
    };

    private void StageExpired(params Report[] reports) =>
        _reports.GetExpiredAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(reports);

    private void StageStale(params Report[] reports) =>
        _reports.GetStalePendingAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(reports);

    private TestableWorker CreateWorker(bool dryRun = false, int batchSize = 500)
    {
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(_provider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var workerOptions = Substitute.For<IOptionsMonitor<WorkerOptions>>();
        workerOptions.Get(nameof(ExpiredReportCleanupWorker))
            .Returns(new WorkerOptions { CronExpression = "0 0 4 * * *" });

        var options = Substitute.For<IOptionsMonitor<ExpiredReportCleanupOptions>>();
        options.CurrentValue.Returns(
            new ExpiredReportCleanupOptions { DryRun = dryRun, BatchSize = batchSize });

        return new TestableWorker(
            workerOptions, options, scopeFactory, _storage, _storageOptions,
            NullLogger<ExpiredReportCleanupWorker>.Instance, new FixedTimeProvider(Now));
    }

    /// <summary>
    /// Exposes the protected sweep, so tests drive it directly rather than through the advisory
    /// lock (which needs a live Postgres connection — the integration suite's territory).
    /// </summary>
    private sealed class TestableWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<ExpiredReportCleanupOptions> options,
        IServiceScopeFactory scopeFactory,
        IReportStorage reportStorage,
        ReportStorageOptions storageOptions,
        Microsoft.Extensions.Logging.ILogger<ExpiredReportCleanupWorker> logger,
        TimeProvider timeProvider)
        : ExpiredReportCleanupWorker(
            workerOptions, options, scopeFactory, reportStorage, storageOptions, logger, timeProvider)
    {
        public Task RunSweepAsync(CancellationToken ct) => SweepAsync(ct);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
