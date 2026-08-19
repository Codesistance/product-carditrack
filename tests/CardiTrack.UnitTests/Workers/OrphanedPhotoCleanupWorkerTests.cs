using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Worker;
using CardiTrack.Worker.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Workers;

/// <summary>
/// Pins the orphan decision and the sweep's failure posture. The worker is the *enforcement*
/// backstop behind the best-effort blob deletes — the properties worth pinning are exactly the
/// ones that make a destructive sweep safe to run unattended: only unreferenced objects die, an
/// in-flight upload's crash window is respected, dry run touches nothing, and one bad object
/// cannot end the run.
/// </summary>
public class OrphanedPhotoCleanupWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 3, 30, 0, TimeSpan.Zero);

    private static readonly string ReferencedName = $"members/{Guid.NewGuid()}/{Guid.NewGuid():N}.jpg";
    private static readonly string OrphanName = $"members/{Guid.NewGuid()}/{Guid.NewGuid():N}.jpg";
    private static readonly string SecondOrphanName = $"members/{Guid.NewGuid()}/{Guid.NewGuid():N}.jpg";

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IProfilePhotoStorage _storage = Substitute.For<IProfilePhotoStorage>();
    private readonly IServiceProvider _provider = Substitute.For<IServiceProvider>();

    public OrphanedPhotoCleanupWorkerTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);

        // Empty estate and empty bucket by default — each test stages only what it is about.
        _members.GetActivePhotoObjectNamesAsync().Returns(Array.Empty<string>());
        _members.GetInactiveWithPhotoAsync().Returns(Array.Empty<CardiMember>());
        StageListing();

        _provider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
    }

    [Fact]
    public async Task Sweep_DeletesAnUnreferencedObjectPastTheGraceWindow_AndSparesTheReferencedOne()
    {
        _members.GetActivePhotoObjectNamesAsync().Returns([ReferencedName]);
        StageListing(
            (ReferencedName, Now.AddDays(-30)),
            (OrphanName, Now.AddDays(-2)));

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _storage.Received(1).DeleteAsync(OrphanName, Arg.Any<CancellationToken>());
        await _storage.DidNotReceive().DeleteAsync(ReferencedName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_SparesAnUnreferencedObjectYoungerThanTheGraceWindow()
    {
        // Unreferenced but one hour old: possibly an upload whose DB save is still in flight.
        StageListing((OrphanName, Now.AddHours(-1)));

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_DryRun_DeletesNothingAndWritesNothing()
    {
        var member = InactiveMemberWithPhoto(SecondOrphanName);
        _members.GetInactiveWithPhotoAsync().Returns([member]);
        StageListing((OrphanName, Now.AddDays(-2)));

        await CreateWorker(dryRun: true).RunSweepAsync(CancellationToken.None);

        await _storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
        Assert.Equal(SecondOrphanName, member.PhotoObjectName); // the row is untouched too
    }

    [Fact]
    public async Task Sweep_ContinuesPastAFailedDelete()
    {
        StageListing(
            (OrphanName, Now.AddDays(-2)),
            (SecondOrphanName, Now.AddDays(-3)));
        _storage.DeleteAsync(OrphanName, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("bucket unavailable for this object"));

        var thrown = await Record.ExceptionAsync(
            () => CreateWorker().RunSweepAsync(CancellationToken.None));

        // One undeletable blob costs itself, not the rest of the sweep — and not the run.
        Assert.Null(thrown);
        await _storage.Received(1).DeleteAsync(SecondOrphanName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_DeletesTheBlobAndClearsTheColumn_ForASoftDeletedMemberStillCarryingAPhoto()
    {
        var member = InactiveMemberWithPhoto(OrphanName);
        _members.GetInactiveWithPhotoAsync().Returns([member]);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        // Even a young blob dies here: the soft-deleted row itself says the photo must not
        // outlive the membership — this is not the crash-window case the grace protects.
        await _storage.Received(1).DeleteAsync(OrphanName, Arg.Any<CancellationToken>());
        Assert.Null(member.PhotoObjectName);
        _members.Received(1).Update(member);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task Sweep_DoesNotDeleteTwice_WhenAClearedLeftoverAlsoAppearsInTheListing()
    {
        var member = InactiveMemberWithPhoto(OrphanName);
        _members.GetInactiveWithPhotoAsync().Returns([member]);
        StageListing((OrphanName, Now.AddDays(-2)));

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _storage.Received(1).DeleteAsync(OrphanName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_SoftDeletedClearFailure_DoesNotStopTheListingSweep_OrGetRetriedByIt()
    {
        var member = InactiveMemberWithPhoto(SecondOrphanName);
        _members.GetInactiveWithPhotoAsync().Returns([member]);
        _storage.DeleteAsync(SecondOrphanName, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("bucket unavailable for this object"));
        // The failed object is also old enough for the listing pass — which must not retry it
        // this run (double-logged, double-counted failure); the row keeps its PhotoObjectName,
        // so the next run is the retry.
        StageListing(
            (OrphanName, Now.AddDays(-2)),
            (SecondOrphanName, Now.AddDays(-2)));

        var thrown = await Record.ExceptionAsync(
            () => CreateWorker().RunSweepAsync(CancellationToken.None));

        Assert.Null(thrown);
        Assert.Equal(SecondOrphanName, member.PhotoObjectName); // column kept: blob-first ordering
        await _storage.Received(1).DeleteAsync(OrphanName, Arg.Any<CancellationToken>());
        await _storage.Received(1).DeleteAsync(SecondOrphanName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_LeavesASoftDeletedMembersPhotoAlone_WhenItsNameIsOutsideTheUploadScheme()
    {
        // The listing pass is scoped to members/ by construction; the DB-driven pass must hold
        // the same line if the column ever carries unexpected data. Blob and column both kept —
        // a human's problem, flagged loudly, not a delete.
        var member = InactiveMemberWithPhoto("backups/postgres/2026-08-17.dump");
        _members.GetInactiveWithPhotoAsync().Returns([member]);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal("backups/postgres/2026-08-17.dump", member.PhotoObjectName);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    private static CardiMember InactiveMemberWithPhoto(string objectName) => new()
    {
        Id = Guid.NewGuid(),
        IsActive = false,
        PhotoObjectName = objectName,
    };

    private void StageListing(params (string ObjectName, DateTimeOffset CreatedAt)[] objects) =>
        _storage.ListAsync(Arg.Any<CancellationToken>()).Returns(ToAsyncEnumerable(objects));

    private static async IAsyncEnumerable<(string ObjectName, DateTimeOffset CreatedAt)> ToAsyncEnumerable(
        (string ObjectName, DateTimeOffset CreatedAt)[] items)
    {
        await Task.CompletedTask; // keeps the iterator honestly async without a real awaitable
        foreach (var item in items)
            yield return item;
    }

    private TestableWorker CreateWorker(bool dryRun = false)
    {
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(_provider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var workerOptions = Substitute.For<IOptionsMonitor<WorkerOptions>>();
        workerOptions.Get(nameof(OrphanedPhotoCleanupWorker))
            .Returns(new WorkerOptions { CronExpression = "0 30 3 * * *" });

        var options = Substitute.For<IOptionsMonitor<OrphanedPhotoCleanupOptions>>();
        options.CurrentValue.Returns(new OrphanedPhotoCleanupOptions { DryRun = dryRun });

        return new TestableWorker(
            workerOptions, options, scopeFactory, _storage,
            NullLogger<OrphanedPhotoCleanupWorker>.Instance, new FixedTimeProvider(Now));
    }

    /// <summary>
    /// Exposes the protected sweep, so tests drive it directly rather than through the advisory
    /// lock (which needs a live Postgres connection — the integration suite's territory).
    /// </summary>
    private sealed class TestableWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<OrphanedPhotoCleanupOptions> options,
        IServiceScopeFactory scopeFactory,
        IProfilePhotoStorage photoStorage,
        Microsoft.Extensions.Logging.ILogger<OrphanedPhotoCleanupWorker> logger,
        TimeProvider timeProvider)
        : OrphanedPhotoCleanupWorker(workerOptions, options, scopeFactory, photoStorage, logger, timeProvider)
    {
        public Task RunSweepAsync(CancellationToken ct) => SweepAsync(ct);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
