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
        _alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns([]);
        _links.GetByCardiMemberIdAsync(_memberId).Returns([]);
    }

    [Fact]
    public async Task AStandingAlertWithNoExplanationIsBackfilled()
    {
        // The case that had no recovery: the model timed out when this alert was raised, and the
        // same finding on a later pass dedups against the alert already stored. Without this
        // sweep the detail screen would show it unexplained for as long as it stood.
        var alert = Standing();
        _alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns([alert]);
        _insights.GetForAlertAsync(alert.Id).Returns((MemberInsight?)null);

        await CreateSut().EvaluateAsync(UtcNow);

        await _insightService.Received(1).RegenerateAlertInsightAsync(alert.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAlertAlreadyExplainedByTheCurrentBriefCostsNothing()
    {
        var alert = Standing();
        _alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns([alert]);
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
        _alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns([alert]);
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
        _alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns(standing);
        _insights.GetForAlertAsync(Arg.Any<Guid>()).Returns((MemberInsight?)null);

        await CreateSut().EvaluateAsync(UtcNow);

        await _insightService.Received(2).RegenerateAlertInsightAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedExplanationDoesNotFailTheMembersPass()
    {
        var alert = Standing();
        _alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns([alert]);
        _insights.GetForAlertAsync(alert.Id).Returns((MemberInsight?)null);
        _insightService.RegenerateAlertInsightAsync(alert.Id, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new HttpRequestException("MedGemma is catching up."));

        // The alert is already stored; a missing explanation is a card the screen leaves out.
        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
    }

    private StatisticalAlertService CreateSut() =>
        new(_unitOfWork, _medicalAi, PromptContextFactory.Composer(_unitOfWork),
            InertStatusLineGenerator.Create(), NullLogger<StatisticalAlertService>.Instance,
            alertEnqueue: null, insights: _insightService);

    private Alert Standing() => new()
    {
        Id = Guid.NewGuid(),
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
