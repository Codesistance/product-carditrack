using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Insights narrate health data through a model, so an unchecked call here leaks more than raw
/// rows would. Composed with the real access service over a mocked unit of work, so these assert
/// the gate as it actually ships rather than a stub of it.
/// </summary>
public class HealthInsightServiceAccessTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IMemberInsightRepository _insights = Substitute.For<IMemberInsightRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _alertId = Guid.NewGuid();

    public HealthInsightServiceAccessTests()
    {
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.MemberInsights.Returns(_insights);

        _links.GetByUserIdAsync(_userId).Returns([
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = true,
                CanViewHealthData = true,
            },
        ]);
        // The outsider is a real, authenticated user — with links of their own, just not to this member.
        _links.GetByUserIdAsync(_outsiderId).Returns([
            new UserCardiMember
            {
                UserId = _outsiderId,
                CardiMemberId = Guid.NewGuid(),
                IsActive = true,
                CanViewHealthData = true,
            },
        ]);

        _alerts.GetByIdWithCardiMemberAsync(_alertId).Returns(new Alert
        {
            Id = _alertId,
            CardiMemberId = _memberId,
            Title = "Steps well below baseline",
            Message = "Steps are 91% below the 30-day baseline.",
        });
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _baselines.GetLatestByCardiMemberAsync(_memberId, Arg.Any<int>()).Returns((PatternBaseline?)null);
        _medicalAi.GenerateStructuredAsync<HealthInsightService.AlertAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HealthInsightService.AlertAiResponse
            {
                Explanation = "Analysis body.",
                RecommendedAction = "Monitor and follow up.",
            });
        _medicalAi.GenerateStructuredAsync<HealthInsightService.BaselineAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HealthInsightService.BaselineAiResponse { Summary = "Analysis body.", KeyFindings = [] });
    }

    private HealthInsightService CreateSut() =>
        new(_medicalAi, _unitOfWork, new CardiMemberAccessService(_unitOfWork),
            PromptContextFactory.Composer(_unitOfWork));

    // ── AnalyzeAlertAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAlert_ServesTheStoredExplanation_ForALinkedUser()
    {
        _insights.GetForAlertAsync(_alertId).Returns(new MemberInsight
        {
            CardiMemberId = _memberId,
            Scope = InsightScope.Alert,
            AlertId = _alertId,
            Summary = "Analysis body.",
            RecommendedAction = "Call them today.",
            GeneratedAtUtc = DateTime.UtcNow,
        });

        var result = await CreateSut().AnalyzeAlertAsync(_userId, _alertId);

        Assert.Equal(_alertId, result.AlertId);
        Assert.Equal("Analysis body.", result.Explanation);
        Assert.Equal("Call them today.", result.RecommendedAction);

        // The whole point of the batch move: opening an alert costs a lookup, never a model call.
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<HealthInsightService.AlertAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeAlert_ComesBackEmpty_WhenThePassHasNotExplainedItYet()
    {
        // An alert raised seconds ago, or one whose explanation the guards withheld. Empty text
        // rather than an error: there is nothing wrong, there is just nothing to say yet.
        var result = await CreateSut().AnalyzeAlertAsync(_userId, _alertId);

        Assert.Equal(_alertId, result.AlertId);
        Assert.Equal(string.Empty, result.Explanation);
        Assert.Equal(string.Empty, result.RecommendedAction);
    }

    [Fact]
    public async Task AnalyzeAlert_Throws_ForAUserNotLinkedToTheAlertsMember()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().AnalyzeAlertAsync(_outsiderId, _alertId));
    }

    [Fact]
    public async Task AnalyzeAlert_SendsNothingToTheModel_WhenAccessIsRefused()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().AnalyzeAlertAsync(_outsiderId, _alertId));

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<HealthInsightService.AlertAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeAlert_ReportsForeignAlertsAndUnknownAlertsIdentically()
    {
        var sut = CreateSut();
        var unknownAlertId = Guid.NewGuid();
        _alerts.GetByIdWithCardiMemberAsync(unknownAlertId).Returns((Alert?)null);

        var foreign = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.AnalyzeAlertAsync(_outsiderId, _alertId));
        var unknown = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.AnalyzeAlertAsync(_outsiderId, unknownAlertId));

        // Same shape of failure either way, so the endpoint cannot be used to test alert ids
        // for existence.
        Assert.Equal(
            foreign.Message.Replace(_alertId.ToString(), "<id>"),
            unknown.Message.Replace(unknownAlertId.ToString(), "<id>"));
    }

    // ── Baseline loading ────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeBaseline_LoadsBaselinesOneAtATime()
    {
        // These repositories share the request's DbContext, and EF Core rejects a second
        // operation while one is in flight. The three lookups used to be started together with
        // Task.WhenAll, so this endpoint threw on every call. A substitute cannot reproduce the
        // EF failure, so the invariant itself is asserted: never two calls open at once.
        var inFlight = 0;
        var overlapped = 0;

        // The task must genuinely stay in flight. Returning a value from a synchronous lambda
        // lets NSubstitute hand back an already-completed task, so each call finishes before the
        // next begins and even Task.WhenAll would look sequential — the assertion would hold
        // against the very code it is meant to reject.
        async Task<PatternBaseline?> TrackedLookupAsync()
        {
            if (Interlocked.Increment(ref inFlight) > 1)
                Interlocked.Exchange(ref overlapped, 1);

            await Task.Delay(25);

            Interlocked.Decrement(ref inFlight);
            return null;
        }

        _baselines.GetLatestByCardiMemberAsync(_memberId, Arg.Any<int>())
            .Returns(_ => TrackedLookupAsync());

        await CreateSut().RegenerateBaselineInsightAsync(_memberId);

        Assert.True(
            Volatile.Read(ref overlapped) == 0,
            "baseline lookups overlapped — concurrent operations on a shared DbContext");
        // The three trend windows, plus the two provisional fallbacks tried because the 30-day
        // lookup returned null — all on the same shared DbContext, so all under this invariant.
        await _baselines.Received(5).GetLatestByCardiMemberAsync(_memberId, Arg.Any<int>());
    }

    [Fact]
    public async Task AnalyzeBaseline_TreatsAMissing30DayBaselineAsStillLearning()
    {
        // Only the 60-day window exists. "Still learning" is defined by the absence of the
        // 30-day baseline specifically, so a longer window must not stand in for it — picking
        // the first available baseline by position would silently claim the member is known.
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 60).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 60,
            AvgSteps = 4200,
        });
        _baselines.GetLatestByCardiMemberAsync(_memberId, 90).Returns((PatternBaseline?)null);

        var written = await CreateSut().RegenerateBaselineInsightAsync(_memberId);

        Assert.True(written);
        await _medicalAi.Received(1).GenerateStructuredAsync<HealthInsightService.BaselineAiResponse>(
            Arg.Is<string>(p => p != null && p.Contains("not yet enough history")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeBaseline_SucceedsWhenNoBaselinesExistAtAll()
    {
        // Every window empty — a brand-new member. Must not index into an empty collection.
        _baselines.GetLatestByCardiMemberAsync(_memberId, Arg.Any<int>()).Returns((PatternBaseline?)null);

        var result = await CreateSut().AnalyzeBaselineAsync(_userId, _memberId);

        Assert.Equal(_memberId, result.CardiMemberId);
    }

    // ── AnalyzeBaselineAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeBaseline_ServesTheStoredReading_ForALinkedUser()
    {
        _insights.GetByScopeAsync(_memberId, InsightScope.Baseline).Returns(new MemberInsight
        {
            CardiMemberId = _memberId,
            Scope = InsightScope.Baseline,
            Summary = "Analysis body.",
            KeyFindings = "Steps are down.\nSleep is steady.",
            BaselinePeriodDays = 30,
            GeneratedAtUtc = DateTime.UtcNow,
        });

        var result = await CreateSut().AnalyzeBaselineAsync(_userId, _memberId);

        Assert.Equal(_memberId, result.CardiMemberId);
        Assert.Equal("Analysis body.", result.Summary);
        Assert.Equal(["Steps are down.", "Sleep is steady."], result.KeyFindings);
        Assert.Equal(30, result.BaselinePeriodDays);

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<HealthInsightService.BaselineAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeBaseline_ReadsAsLearning_BeforeThePassHasWrittenAnything()
    {
        // The honest answer before the first digest pass reaches this member, and the same state
        // the dashboard calls "getting to know you" — so the two surfaces agree from day one.
        var result = await CreateSut().AnalyzeBaselineAsync(_userId, _memberId);

        Assert.Equal(_memberId, result.CardiMemberId);
        Assert.True(result.IsLearning);
        Assert.Empty(result.Summary);
    }

    [Fact]
    public async Task AnalyzeBaseline_WithholdsAStaleReading()
    {
        // Past the staleness ceiling the row describes a picture that has moved on. A member-scoped
        // insight goes quiet rather than claiming to be current — unlike an alert explanation,
        // which is about one fixed moment and never goes out of date.
        _insights.GetByScopeAsync(_memberId, InsightScope.Baseline).Returns(new MemberInsight
        {
            CardiMemberId = _memberId,
            Scope = InsightScope.Baseline,
            Summary = "Analysis body.",
            GeneratedAtUtc = DateTime.UtcNow - InsightServability.MaxAge - TimeSpan.FromHours(1),
        });

        var result = await CreateSut().AnalyzeBaselineAsync(_userId, _memberId);

        Assert.Empty(result.Summary);
        Assert.True(result.IsLearning);
    }

    [Fact]
    public async Task AnalyzeBaseline_Throws_ForAUserNotLinkedToTheMember()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().AnalyzeBaselineAsync(_outsiderId, _memberId));
    }

    [Fact]
    public async Task AnalyzeBaseline_SendsNothingToTheModel_WhenAccessIsRefused()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().AnalyzeBaselineAsync(_outsiderId, _memberId));

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<HealthInsightService.BaselineAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeBaseline_Throws_WhenTheLinkForbidsHealthData()
    {
        _links.GetByUserIdAsync(_userId).Returns([
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = true,
                CanViewHealthData = false,
            },
        ]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().AnalyzeBaselineAsync(_userId, _memberId));
    }
}
