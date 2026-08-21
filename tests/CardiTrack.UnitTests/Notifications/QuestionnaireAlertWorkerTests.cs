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
/// Pins the claim-then-enqueue flow's concurrency and failure-handling semantics — the same shape
/// <see cref="NotificationDispatchWorkerTests"/> covers for the Safety-nudge push sweep, since this
/// worker carries the identical risk: several Worker instances read the same due rows, and a claim
/// spent for a push that never actually went out must come back rather than silently cost the
/// question its next reminder.
/// </summary>
public class QuestionnaireAlertWorkerTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemberQuestionnaireRepository _questionnaires = Substitute.For<IMemberQuestionnaireRepository>();
    private readonly IDispatchService _dispatch = Substitute.For<IDispatchService>();
    private readonly IServiceProvider _provider = Substitute.For<IServiceProvider>();

    public QuestionnaireAlertWorkerTests()
    {
        _unitOfWork.MemberQuestionnaires.Returns(_questionnaires);

        _questionnaires.GetDueForAlertAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MemberQuestionnaire>());

        // Claim won by default — the tests that care about losing it say so explicitly.
        _questionnaires.TryClaimAlertAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // A delivery by default — the tests that care about an empty enqueue say so explicitly.
        _dispatch.EnqueueForQuestionnaireAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new NotificationDelivery()]);

        _provider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
        _provider.GetService(typeof(IDispatchService)).Returns(_dispatch);
    }

    /// <summary>
    /// The 2026-08-12 FirebaseApp shape, applied here: a push stack that fails to construct must
    /// fail before any row is claimed, not claim every due row and then release each one right
    /// back — see the reordering in QuestionnaireAlertWorker.ExecuteJobAsync.
    /// </summary>
    [Fact]
    public async Task ExecuteJob_ResolvesDispatchServiceBeforeClaiming()
    {
        StageDue(DueQuestionnaire());
        _provider.GetService(typeof(IDispatchService))
            .Throws(new InvalidOperationException("Value cannot be null. (Parameter 'Credential must be set')"));

        var thrown = await Record.ExceptionAsync(() => CreateWorker().RunOnceAsync(CancellationToken.None));

        Assert.Null(thrown);
        await _questionnaires.DidNotReceive().TryClaimAlertAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushSweep_ClaimsWithTheReminderCountItRead_ThenEnqueuesTheSameOccurrence()
    {
        var questionnaire = DueQuestionnaire(reminderCount: 1);
        StageDue(questionnaire);

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        await _questionnaires.Received(1).TryClaimAlertAsync(
            questionnaire.Id, 1, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _dispatch.Received(1).EnqueueForQuestionnaireAsync(
            questionnaire.Id, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushSweep_DoesNotEnqueue_WhenAnotherInstanceWonTheClaim()
    {
        // Three Cloud Run instances run this worker and the sweep has no advisory lock, so all
        // three read the same due rows. Only the one whose UPDATE moved the row may enqueue.
        StageDue(DueQuestionnaire());
        _questionnaires.TryClaimAlertAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        await _dispatch.DidNotReceive().EnqueueForQuestionnaireAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushSweep_DoesNotReleaseAClaimItNeverWon()
    {
        // Releasing here would hand back a claim another instance is mid-enqueue on, and that
        // instance's own claim would then be reverted out from under it.
        StageDue(DueQuestionnaire());
        _questionnaires.TryClaimAlertAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _dispatch.EnqueueForQuestionnaireAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("should never be reached"));

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        await _questionnaires.DidNotReceive().ReleaseAlertClaimAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushSweep_ReleasesTheClaim_WhenTheEnqueueThrows()
    {
        // A claim held by an enqueue that never happened would mark the question reminded and
        // stop the sweep looking at it again for a full 24h, having sent nothing.
        var questionnaire = DueQuestionnaire(reminderCount: 0, lastRemindedAtUtc: null);
        StageDue(questionnaire);
        _dispatch.EnqueueForQuestionnaireAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("push stack unavailable"));

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        await _questionnaires.Received(1).ReleaseAlertClaimAsync(
            questionnaire.Id, 0, null, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An empty result — no caregiver has ReceiveAlerts on, or the row stopped qualifying between
    /// the claim and the enqueue call — is typically a standing condition, not a transient one.
    /// Restoring LastRemindedAtUtc to its true pre-claim value here (null, for a never-pushed row)
    /// would make GetDueForAlertAsync treat it as due again on literally the next tick, forever.
    /// The release must instead back off to this tick's own clock, the same 24h cooldown a real
    /// reminder would set — unlike the enqueue-throws case, which does restore the pre-claim value
    /// for an immediate retry, on the presumption that a thrown exception is transient.
    /// </summary>
    [Fact]
    public async Task PushSweep_ReleasesWithABackoff_WhenEnqueueReturnsNoDeliveries()
    {
        var questionnaire = DueQuestionnaire(reminderCount: 0, lastRemindedAtUtc: null);
        StageDue(questionnaire);
        _dispatch.EnqueueForQuestionnaireAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<NotificationDelivery>());

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        await _questionnaires.Received(1).ReleaseAlertClaimAsync(
            questionnaire.Id, 0, FixedNow.UtcDateTime, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushSweep_DoesNotRelease_WhenTheEnqueueDeliversAtLeastOne()
    {
        StageDue(DueQuestionnaire());

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        await _questionnaires.DidNotReceive().ReleaseAlertClaimAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushSweep_KeepsGoing_WhenOneRowsClaimThrows()
    {
        // One scope per row: a shared DbContext would carry a failed row's state into the next
        // one's save, so a single bad row must not cost the rest of the batch.
        var failing = DueQuestionnaire();
        var following = DueQuestionnaire();
        StageDue(failing, following);
        _questionnaires.TryClaimAlertAsync(
                failing.Id, Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("transient database failure"));

        await CreateWorker().RunOnceAsync(CancellationToken.None);

        await _dispatch.Received(1).EnqueueForQuestionnaireAsync(
            following.Id, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static MemberQuestionnaire DueQuestionnaire(
        int reminderCount = 0, DateTime? lastRemindedAtUtc = null) => new()
        {
            Id = Guid.NewGuid(),
            CardiMemberId = Guid.NewGuid(),
            QuestionText = "Has anything changed at home recently?",
            Status = QuestionnaireStatus.Pending,
            GeneratedAtUtc = DateTime.UtcNow.AddHours(-2),
            ReminderCount = reminderCount,
            LastRemindedAtUtc = lastRemindedAtUtc,
        };

    private void StageDue(params MemberQuestionnaire[] due) =>
        _questionnaires.GetDueForAlertAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(due);

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PushSweep_ComputesTheReminderCutoff_FromTheInjectedClock_NotTheWallClock()
    {
        await CreateWorker().RunOnceAsync(CancellationToken.None);

        await _questionnaires.Received(1).GetDueForAlertAsync(
            FixedNow.UtcDateTime, FixedNow.UtcDateTime.AddHours(-24), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    private TestableAlertWorker CreateWorker()
    {
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(_provider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var options = Substitute.For<IOptionsMonitor<WorkerOptions>>();
        options.Get(nameof(QuestionnaireAlertWorker))
            .Returns(new WorkerOptions { CronExpression = "0 */5 * * * *" });

        return new TestableAlertWorker(
            options, scopeFactory, NullLogger<QuestionnaireAlertWorker>.Instance, new FixedTimeProvider(FixedNow));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Exposes the protected tick, so the test drives one sweep rather than the cron loop.</summary>
    private sealed class TestableAlertWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        Microsoft.Extensions.Logging.ILogger<QuestionnaireAlertWorker> logger,
        TimeProvider timeProvider)
        : QuestionnaireAlertWorker(options, scopeFactory, logger, timeProvider)
    {
        public Task RunOnceAsync(CancellationToken ct) => ExecuteJobAsync(ct);
    }
}
