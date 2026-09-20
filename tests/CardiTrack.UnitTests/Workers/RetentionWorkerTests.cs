using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Worker;
using CardiTrack.Worker.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Workers;

/// <summary>
/// Pins the orchestration this worker owns, which is the part the integration suite cannot see:
/// what it asks for, what it refuses to do, and what happens when one account goes wrong.
/// </summary>
/// <remarks>
/// The cascades themselves are covered against a real Postgres (`AccountErasureCascadeTests`,
/// `RetentionPolicyTests`). What is left here is the posture that makes a destructive unattended
/// job safe: the cutoff it computes is the caregiver's own, a dry run deletes nothing, a bad
/// configuration stops the sweep instead of widening it, and one account that throws cannot cost
/// every other account behind it their erasure.
/// </remarks>
public class RetentionWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 5, 0, 0, TimeSpan.Zero);

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IAccountErasureService _accounts = Substitute.For<IAccountErasureService>();
    private readonly IChatRetentionService _chat = Substitute.For<IChatRetentionService>();
    private readonly IMemberInsightRepository _insights = Substitute.For<IMemberInsightRepository>();
    private readonly IServiceProvider _provider = Substitute.For<IServiceProvider>();

    public RetentionWorkerTests()
    {
        _unitOfWork.Users.Returns(_users);
        _unitOfWork.MemberInsights.Returns(_insights);

        // Nothing due and nothing expired by default — each test stages only what it is about.
        _users.GetAccountsDueForErasureAsync(Arg.Any<DateTime>(), Arg.Any<int>()).Returns([]);
        _chat.FindExpiredSessionsAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _insights.GetGeneratedBeforeAsync(Arg.Any<DateTime>(), Arg.Any<int>()).Returns([]);

        _provider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
        _provider.GetService(typeof(IAccountErasureService)).Returns(_accounts);
        _provider.GetService(typeof(IChatRetentionService)).Returns(_chat);
    }

    // ── The two cutoffs ─────────────────────────────────────────────────────────

    /// <summary>
    /// The account cutoff is now minus the caregiver's own grace period — not a number of this
    /// worker's own. Asserting the exact value is what keeps the worker and the sentence the app
    /// shows a caregiver from drifting apart if either is changed alone.
    /// </summary>
    [Fact]
    public async Task Sweep_AsksForAccountsPastTheCaregiversOwnGracePeriod()
    {
        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _users.Received(1).GetAccountsDueForErasureAsync(
            Now.UtcDateTime - UserService.DeletionGracePeriod, 100);
    }

    [Fact]
    public async Task Sweep_AsksForConversationsOlderThanTheConfiguredPeriod()
    {
        await CreateWorker(chatRetentionDays: 90).RunSweepAsync(CancellationToken.None);

        await _chat.Received(1).FindExpiredSessionsAsync(
            Now.UtcDateTime.AddDays(-90), 100, Arg.Any<CancellationToken>());
    }

    // ── Invalid configuration ───────────────────────────────────────────────────

    /// <summary>
    /// A negative retention period moves the cutoff into the future, at which point every
    /// conversation on the platform is expired and a job whose purpose is to delete them would do
    /// exactly that. The sweep has to stop before it reads anything, not clamp and carry on.
    /// </summary>
    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 100)]
    [InlineData(90, 0)]
    [InlineData(90, -5)]
    public async Task Sweep_DoesNothingAtAll_OnInvalidConfiguration(int retentionDays, int batchSize)
    {
        await CreateWorker(chatRetentionDays: retentionDays, batchSize: batchSize)
            .RunSweepAsync(CancellationToken.None);

        await _users.DidNotReceiveWithAnyArgs().GetAccountsDueForErasureAsync(default, default);
        await _chat.DidNotReceiveWithAnyArgs()
            .FindExpiredSessionsAsync(default, default, default);
        await _accounts.DidNotReceiveWithAnyArgs().EraseAsync(default, default);
    }

    // ── Dry run ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The rehearsal switch the data-protection ADR requires: it must reach the operator's log
    /// having touched nothing. Both passes, because a dry run that only half-applies is worse
    /// than none.
    /// </summary>
    [Fact]
    public async Task Sweep_DeletesNothing_InDryRun()
    {
        StageDue(Guid.NewGuid());
        StageExpiredSessions(Guid.NewGuid());

        await CreateWorker(dryRun: true).RunSweepAsync(CancellationToken.None);

        await _accounts.DidNotReceiveWithAnyArgs().EraseAsync(default, default);
        await _chat.DidNotReceiveWithAnyArgs().DeleteSessionsAsync(default!, default, default);
    }

    // ── Erasure ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_ErasesEveryDueAccount()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        StageDue(first, second);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _accounts.Received(1).EraseAsync(first, Arg.Any<CancellationToken>());
        await _accounts.Received(1).EraseAsync(second, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// One account that throws must not cost the accounts behind it their erasure — each of them
    /// is a published thirty-day promise of its own, and a run that stopped at the first failure
    /// would miss all of them for as long as the bad one stayed bad.
    /// </summary>
    [Fact]
    public async Task Sweep_KeepsGoing_WhenOneAccountThrows()
    {
        var bad = Guid.NewGuid();
        var good = Guid.NewGuid();
        StageDue(bad, good);

        _accounts.EraseAsync(bad, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("cascade failed"));

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _accounts.Received(1).EraseAsync(good, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A failing account must not take the chat pass down with it either — the two rules are
    /// independent promises that happen to share a schedule.
    /// </summary>
    [Fact]
    public async Task Sweep_StillRunsTheChatPass_WhenAnAccountThrows()
    {
        StageDue(Guid.NewGuid());
        _accounts.EraseAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("cascade failed"));

        var session = Guid.NewGuid();
        StageExpiredSessions(session);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _chat.Received(1).DeleteSessionsAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(session)),
            Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The belt to the API's braces. `CancelDeletionAsync` already refuses once the window has
    /// closed, so this should never fire — but if the two boundaries are ever changed apart, the
    /// worker reads the account's own row and declines rather than erasing somebody who cancelled.
    /// </summary>
    [Fact]
    public async Task Sweep_SkipsAnAccountWhoseRequestIsNoLongerOutstanding()
    {
        var userId = Guid.NewGuid();
        StageDue(userId);
        _users.GetByIdAsync(userId).Returns(new User { Id = userId, DeletionRequestedAtUtc = null });

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _accounts.DidNotReceiveWithAnyArgs().EraseAsync(default, default);
    }

    // ── Chat ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The cutoff travels to the delete as well as the find, so the service can re-ask. Passing
    /// only the ids is what would let a conversation replied to in between be taken anyway.
    /// </summary>
    [Fact]
    public async Task Sweep_PassesTheSameCutoffToTheDeleteAsToTheFind()
    {
        StageExpiredSessions(Guid.NewGuid());

        await CreateWorker(chatRetentionDays: 90).RunSweepAsync(CancellationToken.None);

        await _chat.Received(1).DeleteSessionsAsync(
            Arg.Any<IReadOnlyList<Guid>>(),
            Now.UtcDateTime.AddDays(-90),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_DoesNotCallTheDelete_WhenNothingHasExpired()
    {
        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _chat.DidNotReceiveWithAnyArgs().DeleteSessionsAsync(default!, default, default);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    private void StageDue(params Guid[] userIds)
    {
        _users.GetAccountsDueForErasureAsync(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(userIds);

        foreach (var userId in userIds)
        {
            // Outstanding by default; the skip test overrides its own.
            _users.GetByIdAsync(userId).Returns(new User
            {
                Id = userId,
                DeletionRequestedAtUtc = Now.UtcDateTime.AddDays(-45),
            });
            _accounts.EraseAsync(userId, Arg.Any<CancellationToken>())
                .Returns(new AccountErasureReport(userId, [], [], [], [], []));
        }
    }

    private void StageExpiredSessions(params Guid[] sessionIds)
    {
        _chat.FindExpiredSessionsAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(sessionIds);
        _chat.DeleteSessionsAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new ChatRetentionReport(sessionIds.Length, 0, 0));
    }

    // ── Stored insights ─────────────────────────────────────────────────────────

    /// <summary>
    /// The period is a constant rather than a configured dial — it is the figure the DPIA records
    /// — so the assertion is on the exact cutoff. If someone changes one without the other, this
    /// is what says so.
    /// </summary>
    [Fact]
    public async Task Sweep_AsksForInsightsPastTheRetentionPeriod()
    {
        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _insights.Received(1).GetGeneratedBeforeAsync(
            Now.UtcDateTime - InsightRetention.MaxAge, InsightRetention.SweepBatchSize);
    }

    [Fact]
    public async Task Sweep_DeletesExpiredInsights_RestatingTheCutoff()
    {
        var expired = Expired(2);
        _insights.GetGeneratedBeforeAsync(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(expired);
        _insights.DeleteGeneratedBeforeAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTime>()).Returns(2);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        // The ids *and* the cutoff: a row the digest or trend pass rewrote between the select and
        // the delete must stop matching rather than be removed by key.
        await _insights.Received(1).DeleteGeneratedBeforeAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2
                && ids.Contains(expired[0].Id)
                && ids.Contains(expired[1].Id)),
            Now.UtcDateTime - InsightRetention.MaxAge);
    }

    [Fact]
    public async Task Sweep_DeletesNoInsights_OnADryRun()
    {
        _insights.GetGeneratedBeforeAsync(Arg.Any<DateTime>(), Arg.Any<int>()).Returns(Expired(1));

        await CreateWorker(dryRun: true).RunSweepAsync(CancellationToken.None);

        await _insights.DidNotReceive().DeleteGeneratedBeforeAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task Sweep_AsksToDeleteNothing_WhenNoInsightHasExpired()
    {
        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _insights.DidNotReceive().DeleteGeneratedBeforeAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task Sweep_LeavesAlertExplanationsToTheRepository()
    {
        // The exemption lives in the query — alert-scoped rows are never selected, because the
        // read path serves an explanation however old it is. Asserted here as the contract the
        // worker relies on, so a later change to either side has to face the other.
        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _insights.Received(1).GetGeneratedBeforeAsync(
            Arg.Any<DateTime>(), InsightRetention.SweepBatchSize);
        await _insights.DidNotReceive().DeleteGeneratedBeforeAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTime>());
    }

    private static List<MemberInsight> Expired(int count) =>
        Enumerable.Range(0, count)
            .Select(_ => new MemberInsight
            {
                Id = Guid.NewGuid(),
                CardiMemberId = Guid.NewGuid(),
                Scope = InsightScope.Baseline,
                Summary = "Written a long time ago.",
                GeneratedAtUtc = Now.UtcDateTime - InsightRetention.MaxAge - TimeSpan.FromDays(1),
            })
            .ToList();

    private TestableWorker CreateWorker(
        bool dryRun = false, int chatRetentionDays = 90, int batchSize = 100,
        int inviteRetentionDays = 30)
    {
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(_provider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var workerOptions = Substitute.For<IOptionsMonitor<WorkerOptions>>();
        workerOptions.Get(nameof(RetentionWorker))
            .Returns(new WorkerOptions { CronExpression = "0 0 5 * * *" });

        var options = Substitute.For<IOptionsMonitor<RetentionWorkerOptions>>();
        options.CurrentValue.Returns(new RetentionWorkerOptions
        {
            DryRun = dryRun,
            ChatRetentionDays = chatRetentionDays,
            BatchSize = batchSize,
        });

        var inviteOptions = Substitute.For<IOptionsMonitor<DeviceInviteOptions>>();
        inviteOptions.CurrentValue.Returns(new DeviceInviteOptions
        {
            RetentionDays = inviteRetentionDays,
        });

        return new TestableWorker(
            workerOptions, options, inviteOptions, scopeFactory,
            NullLogger<RetentionWorker>.Instance, new FixedTimeProvider(Now));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Exposes the protected sweep, so tests drive it directly rather than through the advisory
    /// lock (which needs a live Postgres connection — the integration suite's territory).
    /// </summary>
    private sealed class TestableWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<RetentionWorkerOptions> options,
        IOptionsMonitor<DeviceInviteOptions> inviteOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<RetentionWorker> logger,
        TimeProvider timeProvider)
        : RetentionWorker(workerOptions, options, inviteOptions, scopeFactory, logger, timeProvider)
    {
        public Task RunSweepAsync(CancellationToken ct) => SweepAsync(ct);
    }
}
