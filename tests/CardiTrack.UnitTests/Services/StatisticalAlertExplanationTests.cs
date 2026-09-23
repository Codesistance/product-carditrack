using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Who gets an explanation written for them, and how often. The pass that raises an alert is the
/// only thing that explains it, so what these pin is the recovery path: an explanation lost to a
/// model timeout must not be lost for good, and retrying it must not cost a call on every one of
/// the 288 passes a day.
/// </summary>
public class StatisticalAlertExplanationTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IAlertPreferenceRepository _alertPreferences = Substitute.For<IAlertPreferenceRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IMemberInsightRepository _insights = Substitute.For<IMemberInsightRepository>();
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();
    private readonly IHealthInsightService _insightService = Substitute.For<IHealthInsightService>();

    private readonly Guid _memberId = Guid.NewGuid();
    private static readonly DateTime UtcNow = new(2026, 9, 20, 13, 40, 0, DateTimeKind.Utc);

    public StatisticalAlertExplanationTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.AlertPreferences.Returns(_alertPreferences);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.MemberInsights.Returns(_insights);

        // A member with nothing off: no findings, no new alerts, so every test here is about the
        // standing alerts the pass sweeps rather than about the rules.
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
        });
        _baselines.GetLatestByCardiMemberAsync(_memberId, Arg.Any<int>()).Returns((PatternBaseline?)null);
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([]);

        // The backfill sweep has its own candidate set now — who holds a readable alert, not who
        // has recent readings.
        _alerts.GetCardiMemberIdsWithServableAlertsAsync(Arg.Any<DateTime>()).Returns([_memberId]);
        _alerts.GetServableByCardiMemberAsync(_memberId, Arg.Any<DateTime>()).Returns([]);
        _links.GetByCardiMemberIdAsync(_memberId).Returns([]);
    }

    [Fact]
    public async Task AStandingAlertWithNoExplanationIsBackfilled()
    {
        // The case that had no recovery: the model timed out when this alert was raised, and the
        // same finding on a later pass dedups against the alert already stored. Without this
        // sweep the detail screen would show it unexplained for as long as it stood.
        var alert = Standing();
        _alerts.GetServableByCardiMemberAsync(_memberId, Arg.Any<DateTime>()).Returns([alert]);
        _insights.GetForAlertAsync(alert.Id).Returns((MemberInsight?)null);

        await CreateSut().EvaluateAsync(UtcNow);

        await _insightService.Received(1).RegenerateAlertInsightAsync(alert.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAlertAlreadyExplainedByTheCurrentBriefCostsNothing()
    {
        var alert = Standing();
        _alerts.GetServableByCardiMemberAsync(_memberId, Arg.Any<DateTime>()).Returns([alert]);
        _insights.GetForAlertAsync(alert.Id).Returns(new MemberInsight
        {
            CardiMemberId = _memberId,
            Scope = InsightScope.Alert,
            AlertId = alert.Id,
            Summary = "Already said.",
            GeneratedAtUtc = UtcNow.AddHours(-1),
            PromptVersion = HealthInsightService.AlertPromptVersion,
        });

        await CreateSut().EvaluateAsync(UtcNow);

        await _insightService.DidNotReceive().RegenerateAlertInsightAsync(
            alert.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAlertExplainedByAnOlderBriefIsRewritten()
    {
        var alert = Standing();
        _alerts.GetServableByCardiMemberAsync(_memberId, Arg.Any<DateTime>()).Returns([alert]);
        _insights.GetForAlertAsync(alert.Id).Returns(new MemberInsight
        {
            CardiMemberId = _memberId,
            Scope = InsightScope.Alert,
            AlertId = alert.Id,
            Summary = "Said by last month's brief.",
            GeneratedAtUtc = UtcNow.AddHours(-1),
            PromptVersion = HealthInsightService.AlertPromptVersion - 1,
        });

        await CreateSut().EvaluateAsync(UtcNow);

        await _insightService.Received(1).RegenerateAlertInsightAsync(alert.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheBackfillIsCappedPerPass()
    {
        // A member with a backlog catches up over several passes rather than paying for all of it
        // at once — and an alert whose reply keeps failing the guards cannot drag every other
        // standing alert into a model call on all 288 passes a day.
        var standing = Enumerable.Range(0, 6).Select(_ => Standing()).ToList();
        _alerts.GetServableByCardiMemberAsync(_memberId, Arg.Any<DateTime>()).Returns(standing);
        _insights.GetForAlertAsync(Arg.Any<Guid>()).Returns((MemberInsight?)null);

        await CreateSut().EvaluateAsync(UtcNow);

        await _insightService.Received(2).RegenerateAlertInsightAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The cap plus a stable order starves the tail. Two alerts whose replies keep failing the
    /// guards stay candidates forever, and taking the first two every pass would spend the whole
    /// budget on them while a third standing alert behind them was never once attempted.
    /// </summary>
    [Fact]
    public async Task APermanentlyFailingAlertDoesNotStarveTheOnesBehindIt()
    {
        var standing = Enumerable.Range(0, 3)
            .Select(i => Standing(createdAt: UtcNow.AddHours(-(3 - i))))
            .ToList();
        _alerts.GetServableByCardiMemberAsync(_memberId, Arg.Any<DateTime>()).Returns(standing);

        // Nothing is ever written, so all three are candidates on every pass — the shape of a
        // reply the guards keep rejecting.
        _insights.GetForAlertAsync(Arg.Any<Guid>()).Returns((MemberInsight?)null);
        _insightService.RegenerateAlertInsightAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var attempted = new HashSet<Guid>();
        _insightService
            .When(s => s.RegenerateAlertInsightAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()))
            .Do(call => attempted.Add(call.Arg<Guid>()));

        // Three consecutive passes at the pass cadence. Two attempts each is more than the three
        // candidates, so every one of them has had a turn well inside this.
        var sut = CreateSut();
        for (var pass = 0; pass < 3; pass++)
            await sut.EvaluateAsync(UtcNow.AddMinutes(5 * pass));

        Assert.Equal(standing.Select(a => a.Id).ToHashSet(), attempted);
    }

    /// <summary>Consecutive passes take consecutive slices, so the rotation walks rather than jumps.</summary>
    [Fact]
    public void TheRotationAdvancesOnePassAtATime()
    {
        var offsets = Enumerable.Range(0, 5)
            .Select(pass => StatisticalAlertService.RotationOffset(UtcNow.AddMinutes(5 * pass), 5))
            .ToList();

        Assert.Equal(5, offsets.Distinct().Count());
    }

    [Fact]
    public void TheRotationHoldsAtZeroWithNothingToRotate()
    {
        Assert.Equal(0, StatisticalAlertService.RotationOffset(UtcNow, candidateCount: 0));
    }

    [Fact]
    public async Task AFailedExplanationDoesNotFailTheMembersPass()
    {
        var alert = Standing();
        _alerts.GetServableByCardiMemberAsync(_memberId, Arg.Any<DateTime>()).Returns([alert]);
        _insights.GetForAlertAsync(alert.Id).Returns((MemberInsight?)null);
        _insightService.RegenerateAlertInsightAsync(alert.Id, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new HttpRequestException("MedGemma is catching up."));

        // The alert is already stored; a missing explanation is a card the screen leaves out.
        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
    }

    private StatisticalAlertService CreateSut() =>
        new(_unitOfWork, _medicalAi, _rewriteAi, PromptContextFactory.Composer(_unitOfWork),
            InertStatusLineGenerator.Create(), NullLogger<StatisticalAlertService>.Instance, new PassThroughWriteGuard(),
            alertEnqueue: null, insights: _insightService);

    /// <summary>
    /// Resolving an alert closes the episode; it does not close the card. AlertService.GetByIdAsync
    /// gates on IsActive alone and AlertResolution never touches it, so a caregiver can still open
    /// a resolved alert months later — and device silence resolves the moment the watch reports
    /// again, often within one pass of firing. Asking only for unresolved alerts here meant an
    /// explanation that failed just before that could never be retried, on a card that goes on
    /// being served for good.
    /// </summary>
    [Fact]
    public async Task TheSweepAsksForWhatACaregiverCanStillOpen_NotOnlyWhatIsUnresolved()
    {
        await CreateSut().EvaluateAsync(UtcNow);

        await _alerts.Received(1).GetServableByCardiMemberAsync(_memberId, Arg.Any<DateTime>());
        await _alerts.DidNotReceive().GetUnresolvedByCardiMemberAsync(_memberId);
    }

    [Fact]
    public async Task TheWindowIsBoundedSoTheWalkCannotGrowWithTheArchive()
    {
        // Every candidate costs an indexed lookup on every one of the 288 passes a day, so the
        // cutoff is what stops this scaling with a member's whole alert history. A fortnight back
        // from the pass clock, not from nothing.
        await CreateSut().EvaluateAsync(UtcNow);

        await _alerts.Received(1).GetServableByCardiMemberAsync(
            _memberId,
            Arg.Is<DateTime>(cutoff =>
                cutoff > UtcNow.AddDays(-15) && cutoff < UtcNow.AddDays(-13)));
    }

    [Fact]
    public async Task ARecentlyResolvedAlertStillGetsItsExplanationBackfilled()
    {
        // The case that was lost for good: raised, its explanation failed, then the producer
        // resolved it before the next pass reached it.
        var resolved = Standing();
        resolved.IsResolved = true;
        _alerts.GetServableByCardiMemberAsync(_memberId, Arg.Any<DateTime>()).Returns([resolved]);
        _insights.GetForAlertAsync(resolved.Id).Returns((MemberInsight?)null);

        await CreateSut().EvaluateAsync(UtcNow);

        await _insightService.Received(1).RegenerateAlertInsightAsync(
            resolved.Id, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The sweep must not ride the rule pass's candidate filter. Those members are the ones with
    /// readings in the last two days, which is right for judging today's data and wrong here: an
    /// alert stays readable long after the readings stop, and `device_silence` stays unresolved
    /// precisely because they have stopped. The member whose watch has been quiet for three days
    /// is the most likely to be holding an unexplained alert and was the first one dropped.
    /// </summary>
    [Fact]
    public async Task AMemberWithNoRecentReadingsIsStillSwept()
    {
        // Out of the rule pass entirely — no activity in the window it asks for.
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([]);

        var alert = Standing();
        _alerts.GetServableByCardiMemberAsync(_memberId, Arg.Any<DateTime>()).Returns([alert]);
        _insights.GetForAlertAsync(alert.Id).Returns((MemberInsight?)null);

        await CreateSut().EvaluateAsync(UtcNow);

        await _insightService.Received(1).RegenerateAlertInsightAsync(
            alert.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheSweepStillStopsAtAPausedMember()
    {
        // An explanation is something said about someone being watched, and pausing monitoring is
        // them asking us to stop. The rule pass applies that gate; decoupling must not lose it.
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
            MonitoringPausedUntil = UtcNow.AddDays(3),
        });

        var alert = Standing();
        _alerts.GetServableByCardiMemberAsync(_memberId, Arg.Any<DateTime>()).Returns([alert]);
        _insights.GetForAlertAsync(alert.Id).Returns((MemberInsight?)null);

        await CreateSut().EvaluateAsync(UtcNow);

        await _insightService.DidNotReceive().RegenerateAlertInsightAsync(
            alert.Id, Arg.Any<CancellationToken>());
    }

    private Alert Standing(DateTime? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        CreatedDate = createdAt ?? UtcNow.AddHours(-3),
        CardiMemberId = _memberId,
        AlertType = AlertType.Inactivity,
        Severity = AlertSeverity.Yellow,
        Title = "Quieter than usual",
        Message = "They moved less than they normally do.",
        TriggeredDate = UtcNow.AddHours(-3),
        MetricValues = """{"rule":"activity_decline","steps":1200,"baselineAvgSteps":5000}""",
        IsActive = true,
    };
}
