using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The trend pass as it ships. The narrative itself is the model's, but what may reach a family —
/// and what may never — is this service's, so these pin the guards and the gates rather than the
/// prose: nothing before a month of history, nothing that names a condition, and never a score.
/// </summary>
public class TrendInterpretationServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IMemberInsightRepository _insights = Substitute.For<IMemberInsightRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IMemberQuestionnaireRepository _questionnaires =
        Substitute.For<IMemberQuestionnaireRepository>();

    private readonly Guid _memberId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 9, 20, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    /// <summary>
    /// The last completed day, which is where the window ends — today's row holds only however
    /// far through the day the job has run.
    /// </summary>
    private static readonly DateOnly Through = Today.AddDays(-1);

    public TrendInterpretationServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.MemberInsights.Returns(_insights);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.MemberQuestionnaires.Returns(_questionnaires);

        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
        });
        _baselines.GetLatestByCardiMemberAsync(_memberId, Arg.Any<int>()).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 30,
            AvgSteps = 5000,
        });
        WithHistory(60);
        WithReply("Their steps have eased off over the last few weeks.", ["Steps are down."]);
    }

    [Fact]
    public async Task AMonthOfHistoryEarnsANarrative()
    {
        var written = await CreateSut().InterpretMemberAsync(_memberId, Now);

        Assert.True(written);
        var stored = StoredInsight();
        Assert.Equal(InsightScope.Trend, stored.Scope);
        Assert.Equal("Their steps have eased off over the last few weeks.", stored.Summary);
        Assert.Equal("Steps are down.", stored.KeyFindings);
        // A trend is never the learning state: the pass declines outright rather than narrating
        // a member it does not know yet.
        Assert.False(stored.IsLearning);
    }

    [Fact]
    public async Task AMemberStillBeingLearnedIsNotNarratedAtAll()
    {
        WithHistory(TrendFeatureCalculator.MinimumDaysForTrend - 1);

        var written = await CreateSut().InterpretMemberAsync(_memberId, Now);

        Assert.False(written);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<TrendInterpretationService.TrendAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ANarrativeWrittenTodayIsNotPaidForTwice()
    {
        _insights.GetByScopeAsync(_memberId, InsightScope.Trend).Returns(new MemberInsight
        {
            CardiMemberId = _memberId,
            Scope = InsightScope.Trend,
            Summary = "Written a few hours ago.",
            GeneratedAtUtc = Now.AddHours(-2),
            PromptVersion = TrendInterpretationService.CurrentPromptVersion,
        });

        Assert.False(await CreateSut().InterpretMemberAsync(_memberId, Now));
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<TrendInterpretationService.TrendAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARowFromAnOlderBriefIsRewrittenWhateverItsAge()
    {
        // Including a change to the pinned reference table, which the version folds in: a
        // narrative written against superseded published figures must not outlive them.
        _insights.GetByScopeAsync(_memberId, InsightScope.Trend).Returns(new MemberInsight
        {
            CardiMemberId = _memberId,
            Scope = InsightScope.Trend,
            Summary = "Written minutes ago, by last month's brief.",
            GeneratedAtUtc = Now.AddMinutes(-5),
            PromptVersion = TrendInterpretationService.CurrentPromptVersion - 1,
        });

        Assert.True(await CreateSut().InterpretMemberAsync(_memberId, Now));
    }

    [Fact]
    public async Task ANarrativeThatNamesAConditionIsWithheld()
    {
        WithReply("This looks like atrial fibrillation developing.", []);

        Assert.False(await CreateSut().InterpretMemberAsync(_memberId, Now));
        await _insights.DidNotReceive().AddAsync(Arg.Any<MemberInsight>());
    }

    /// <summary>
    /// The one comparison the brief allows, said in the same breath as the refusal it sits beside.
    /// </summary>
    /// <remarks>
    /// Asking for a figure against a published range while also saying "never work out a
    /// comparison" left the model two instructions that cannot both be followed, and the likely
    /// casualty is the new one. Placing a given number against a given range is reading two
    /// figures against each other; the refusal is about calculating a third.
    /// </remarks>
    [Fact]
    public async Task ThePromptAllowsTheRangeComparisonItAsksFor()
    {
        await CreateSut().InterpretMemberAsync(_memberId, Now);

        var prompt = CapturedPrompt();
        Assert.Contains("The one comparison you may make is placing", prompt);
        Assert.Contains("rather than calculating a third", prompt);

        // The refusal itself is narrowed, not dropped.
        Assert.Contains("Never work out a percentage, a difference or a direction yourself", prompt);
        Assert.Contains("introduce a number that is not in front of you", prompt);
    }

    /// <summary>
    /// Steady and outside guidance is the case the whole change exists for, so the summary must
    /// not reach for the published range only when something has moved.
    /// </summary>
    [Fact]
    public async Task ThePromptAsksForTheRangeWhetherOrNotTheMetricMoved()
    {
        await CreateSut().InterpretMemberAsync(_memberId, Now);

        var prompt = CapturedPrompt();
        Assert.Contains("whether or not it has", prompt);
        Assert.Contains("sitting outside guidance while holding perfectly steady", prompt);
    }

    /// <summary>
    /// Two metrics have no published range on purpose, so the rule for an empty list cannot ask
    /// whether they sit inside one.
    /// </summary>
    [Fact]
    public async Task TheEmptyListRuleOnlyAsksAboutMetricsThatHaveARange()
    {
        await CreateSut().InterpretMemberAsync(_memberId, Now);

        var prompt = CapturedPrompt();
        Assert.Contains("metric that has a published range sits inside it", prompt);
        Assert.Contains("judged on movement alone", prompt);
    }

    /// <summary>
    /// The brief asks for each figure against the published ranges, not only against their own
    /// usual.
    /// </summary>
    /// <remarks>
    /// The ranges were always in the prompt and nothing told the model to use them, so a
    /// narrative reported five hours of sleep a night without mentioning that seven to nine is
    /// what the NSF recommends at that age — three findings that all read "lower than usual" and
    /// gave a family nothing to act on.
    /// </remarks>
    [Fact]
    public async Task ThePromptAsksForTheFigureAgainstThePublishedRange()
    {
        await CreateSut().InterpretMemberAsync(_memberId, Now);

        var prompt = CapturedPrompt();
        Assert.Contains("say where their figure sits against", prompt);
        Assert.Contains("name the body it comes from", prompt);

        // Both yardsticks, and what each one answers: a reading can be down on their usual and
        // still inside the published range, or steady for them and outside it.
        Assert.Contains("usual says whether this is a change for them", prompt);
        Assert.Contains("published ranges say whether it sits", prompt);
    }

    /// <summary>
    /// Where the table carries no range, the model is told to say nothing rather than reach for
    /// one. Two metrics have none on purpose — overnight HRV, which no body publishes an adult
    /// band for, and breathing asleep, whose only published figure is measured at rest.
    /// </summary>
    [Fact]
    public async Task ThePromptForbidsSupplyingARangeTheTableWithheld()
    {
        await CreateSut().InterpretMemberAsync(_memberId, Now);

        var prompt = CapturedPrompt();
        Assert.Contains("that absence is deliberate", prompt);
        Assert.Contains("supply in its place", prompt);

        // And the table really does withhold those two, so the instruction is not hypothetical.
        Assert.Contains(HealthReferenceRanges.NoHeartRateVariabilityBand, prompt);
        Assert.Contains(HealthReferenceRanges.NoOvernightBreathingBand, prompt);
    }

    /// <summary>
    /// The line this change had to stay on the right side of.
    /// </summary>
    /// <remarks>
    /// Saying a figure sits outside a published range is a fact about the figure. Saying what it
    /// might lead to, how likely that is, or what it puts someone at risk of is prognosis — which
    /// <c>docs/llm_design.md</c> excludes from this pass ("no risk scores"), and which the DPIA
    /// flags as the thing that can make software a regulated device under EU MDR Rule 11 even
    /// without diagnosing (open item OI-2). Asking for the comparison must not have loosened it.
    /// </remarks>
    [Fact]
    public async Task ThePromptStillRefusesRiskAndPrognosis()
    {
        await CreateSut().InterpretMemberAsync(_memberId, Now);

        var prompt = CapturedPrompt();
        Assert.Contains("Never name a condition, a diagnosis or a treatment", prompt);
        Assert.Contains("risk level or a prediction of what will happen next", prompt);

        // And the boundary spelled out, because the new instruction sits right beside it.
        Assert.Contains("is a fact about the figure and is wanted", prompt);
        Assert.Contains("what it puts them at risk of is none of those things", prompt);
    }

    [Fact]
    public void ChangingTheBriefRetiresTheNarrativesWrittenBeforeIt()
    {
        // A stored narrative that never mentioned a published range must not outlive the brief
        // that now asks for one; nor may one written before the opening named its own stretch
        // outlive the brief that does. Brief 3's rolling wording is byte-for-byte brief 2's, so
        // this is the case the stamp exists for — nothing in the text would give the staleness
        // away, and only the number retires the row.
        Assert.Equal(3, TrendInterpretationService.BriefVersion);
        Assert.True(
            TrendInterpretationService.CurrentPromptVersion > 200 + PinnedReferenceTable.Version,
            "the stamp must exceed everything written under brief 2, whatever the table version.");
    }

    [Fact]
    public async Task ThePromptCarriesTheComputedFiguresAndThePinnedRanges_AndForbidsScores()
    {
        await CreateSut().InterpretMemberAsync(_memberId, Now);

        var prompt = CapturedPrompt();
        Assert.Contains("Computed trend features", prompt);
        Assert.Contains("days with readings", prompt);
        Assert.Contains($"Pinned reference ranges (version {PinnedReferenceTable.Version})", prompt);
        Assert.Contains("Do not recall others", prompt);

        // The two standing refusals from the design's "what is never produced". Asserted in
        // fragments that sit within one line of the brief: the raw literal wraps, so a longer
        // quotation would fail on the newline rather than on the meaning.
        Assert.Contains("Never give a score, a probability", prompt);
        Assert.Contains("risk level or a prediction of what will happen next", prompt);
        Assert.Contains("Never name a condition", prompt);

        // And the instruction that keeps the model reading arithmetic rather than doing any. It
        // no longer forbids comparisons outright: placing a figure against a range printed beside
        // it is reading two given numbers, and the brief now asks for exactly that.
        Assert.Contains("Never work out a percentage, a difference or a direction yourself", prompt);
    }

    [Fact]
    public async Task APausedMemberIsLeftAlone()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
            MonitoringPausedUntil = Now.AddDays(3),
        });

        Assert.False(await CreateSut().InterpretMemberAsync(_memberId, Now));
    }

    /// <summary>
    /// The window stops at the last completed local day. Today's row is a part-day — a morning's
    /// steps and nothing else — and it is the newest point in every moving average and the last
    /// point the slope is fitted through, so including it reads as a decline that is only the
    /// clock. BaselineCalculationWorker ends a day back for the same reason, and these deviations
    /// are measured against those baselines.
    /// </summary>
    [Fact]
    public async Task TheWindowEndsOnTheLastCompletedDay_NotTodaysPartOfADay()
    {
        await CreateSut().InterpretMemberAsync(_memberId, Now);

        var range = _activityLogs.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name
                == nameof(IActivityLogRepository.GetByCardiMemberAndDateRangeAsync))
            .GetArguments();

        var from = (DateOnly)range[1]!;
        var through = (DateOnly)range[2]!;

        Assert.Equal(Today.AddDays(-1), through);
        Assert.Equal(through.AddDays(-(TrendInterpretationService.TrendWindowDays - 1)), from);
    }

    private TrendInterpretationService CreateSut() =>
        new(_unitOfWork, _medicalAi, PromptContextFactory.Composer(_unitOfWork),
            NullLogger<TrendInterpretationService>.Instance,
            new PassThroughWriteGuard());

    private void WithHistory(int days)
    {
        var logs = Enumerable.Range(0, days)
            .Select(offset => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Through.AddDays(-(days - 1 - offset)),
                Steps = 4000,
                RestingHeartRate = 62,
            })
            .ToList();

        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(logs);
    }

    private void WithReply(string summary, List<string> findings) =>
        _medicalAi.GenerateStructuredAsync<TrendInterpretationService.TrendAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TrendInterpretationService.TrendAiResponse
            {
                Summary = summary,
                KeyFindings = findings,
            });

    private MemberInsight StoredInsight() =>
        _insights.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IMemberInsightRepository.AddAsync))
            .Select(call => (MemberInsight)call.GetArguments()[0]!)
            .Last();

    private string CapturedPrompt() =>
        (string)_medicalAi.ReceivedCalls().First().GetArguments()[0]!;
}
