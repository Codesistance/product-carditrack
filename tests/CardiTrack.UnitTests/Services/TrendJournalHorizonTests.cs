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
/// The weekly and monthly trend narratives, which ride the half-hourly digest pass rather than the
/// daily trend job. What these pin is the due rule and the guards around it: the pass runs
/// forty-eight times a day and a member stays due for the rest of their local day once their hour
/// passes, so "writes once" and "writes only when due" are the two properties that keep this from
/// being forty-eight model calls a week per member.
/// </summary>
public class TrendJournalHorizonTests
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

    /// <summary>2026-09-07 is a Monday, which is JournalSchedule.DefaultWeekStartsOn, and 04:00
    /// is past the 02:00 default write time — so the default member is due at this instant.</summary>
    private static readonly DateTime MondayMorning = new(2026, 9, 7, 4, 0, 0, DateTimeKind.Utc);

    /// <summary>The first of a month, same hour — the monthly horizon's due instant.</summary>
    private static readonly DateTime FirstOfMonth = new(2026, 9, 1, 4, 0, 0, DateTimeKind.Utc);

    public TrendJournalHorizonTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        GenerationLeaseStub.GrantAll(_unitOfWork);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.MemberInsights.Returns(_insights);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.MemberQuestionnaires.Returns(_questionnaires);

        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(_ => Member());
        _baselines.GetLatestByCardiMemberAsync(_memberId, Arg.Any<int>()).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 30,
            AvgSteps = 5000,
        });

        WithFullHistory();
        WithReply("Their steps eased off across the week.", ["Steps are down."]);
    }

    [Fact]
    public async Task AWeekIsNarratedOnTheMembersOwnWeekStart()
    {
        var written = await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning);

        Assert.Equal(1, written);
        var stored = StoredInsight();
        Assert.Equal(InsightScope.TrendWeekly, stored.Scope);
        Assert.Equal("Their steps eased off across the week.", stored.Summary);
    }

    [Fact]
    public async Task AMonthIsNarratedOnTheFirst()
    {
        var written = await CreateSut().InterpretDueJournalHorizonsAsync(FirstOfMonth);

        Assert.Equal(1, written);
        Assert.Equal(InsightScope.TrendMonthly, StoredInsight().Scope);
    }

    [Fact]
    public async Task NothingIsWrittenOnADayThatIsNeitherAWeekStartNorTheFirst()
    {
        // A Wednesday in the middle of a month: no horizon can be due, and no model is called.
        var written = await CreateSut()
            .InterpretDueJournalHorizonsAsync(new DateTime(2026, 9, 16, 4, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, written);
        Assert.Empty(_medicalAi.ReceivedCalls());
    }

    [Fact]
    public async Task NothingIsWrittenBeforeTheMembersChosenHour()
    {
        // Their week start, but they asked for 06:00 and the pass is running at 04:00. The next
        // half-hourly tick that clears their hour is the one that writes it.
        _members.GetByIdAsync(_memberId).Returns(_ => Member(weekbookTime: new TimeOnly(6, 0)));

        Assert.Equal(0, await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning));
        Assert.Empty(_medicalAi.ReceivedCalls());
    }

    [Fact]
    public async Task AWeekIsWrittenOnceHoweverManyTimesThePassRuns()
    {
        // The member stays due for the rest of their local day, and the digest pass runs every
        // half hour. Without the once-per-local-day check this is twenty model calls for one week.
        _insights.GetByScopeAsync(_memberId, InsightScope.TrendWeekly).Returns(new MemberInsight
        {
            CardiMemberId = _memberId,
            Scope = InsightScope.TrendWeekly,
            Summary = "Already written this morning.",
            GeneratedAtUtc = MondayMorning.AddHours(-1),
            PromptVersion = TrendInterpretationService.CurrentPromptVersion,
        });

        Assert.Equal(0, await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning));
        Assert.Empty(_medicalAi.ReceivedCalls());
    }

    [Fact]
    public async Task AMemberAnotherExecutionIsAlreadyGeneratingIsLeftToIt()
    {
        // The overlapping-execution case. The digest job is scheduled every half hour against an
        // hour's Cloud Run timeout, so both executions can pass the already-written probe before
        // either writes. The claim is what stops the second paying for the same generation, and
        // the point of this test is that it is refused *before* the model call, not after.
        _unitOfWork.GenerationLeases
            .TryClaimAsync(
                Arg.Any<Guid>(), Arg.Any<GenerationWork>(), Arg.Any<DateOnly>(),
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Assert.Equal(0, await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning));
        Assert.Empty(_medicalAi.ReceivedCalls());
    }

    [Fact]
    public async Task TheClaimIsTakenForTheHorizonAndPeriodBeingWritten()
    {
        await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning);

        // The week that ended last night, under its own work kind — so a member's Weekbook and
        // their weekly trend, due on the same instant, do not block each other.
        await _unitOfWork.GenerationLeases.Received(1).TryClaimAsync(
            _memberId, GenerationWork.TrendWeekly, new DateOnly(2026, 9, 6),
            MondayMorning, GenerationLeaseTerm.Default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheClaimIsGivenBackEvenWhenTheGenerationFails()
    {
        // Released in a finally rather than on success, so a member whose narrative failed is
        // retried next pass instead of waiting out the lease.
        _medicalAi.GenerateStructuredAsync<TrendInterpretationService.TrendAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<TrendInterpretationService.TrendAiResponse>(_ => throw new HttpRequestException("busy"));

        Assert.Equal(0, await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning));

        await _unitOfWork.GenerationLeases.Received(1).ReleaseAsync(
            _memberId, GenerationWork.TrendWeekly, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheLocalDayStartSurvivesAFallBackDay()
    {
        // The bug the once-a-day check would otherwise carry. On 2026-10-25 UK clocks go back, so
        // that local day runs 25 hours. At 23:00 local the wall clock has advanced 23 hours while
        // 24 have passed — so subtracting the local time of day, which is the same arithmetic only
        // while the offset has not moved, puts the day's start an hour late and a narrative
        // written at 00:30 reads as belonging to the day before. That is the duplicate this check
        // exists to prevent, on exactly the day it claimed to handle.
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var lateOnTheLongDay = new DateTime(2026, 10, 25, 23, 0, 0, DateTimeKind.Unspecified);

        var dayStart = TrendInterpretationService.LocalDayStartUtc(lateOnTheLongDay, london);

        // Local midnight on 25 October is 23:00 UTC on the 24th — BST is still in force at that
        // instant, an hour ahead.
        Assert.Equal(new DateTime(2026, 10, 24, 23, 0, 0, DateTimeKind.Utc), dayStart);

        // And a narrative written just after that midnight falls inside the day, which is what
        // stops it being written twice.
        var justAfterMidnightUtc = new DateTime(2026, 10, 24, 23, 30, 0, DateTimeKind.Utc);
        Assert.True(justAfterMidnightUtc >= dayStart);
    }

    [Fact]
    public void TheLocalDayStartIsExactOnAnOrdinaryDay()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

        // Midwinter, GMT, no offset in play: local midnight is midnight UTC.
        Assert.Equal(
            new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            TrendInterpretationService.LocalDayStartUtc(
                new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Unspecified), london));

        // Midsummer, BST, an hour ahead.
        Assert.Equal(
            new DateTime(2026, 6, 14, 23, 0, 0, DateTimeKind.Utc),
            TrendInterpretationService.LocalDayStartUtc(
                new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Unspecified), london));
    }

    [Fact]
    public async Task LastWeeksNarrativeDoesNotStandInForThisWeeks()
    {
        // Seven days old and written by the current brief — but it is the previous period's, and
        // the member's local day has turned over many times since.
        _insights.GetByScopeAsync(_memberId, InsightScope.TrendWeekly).Returns(new MemberInsight
        {
            CardiMemberId = _memberId,
            Scope = InsightScope.TrendWeekly,
            Summary = "Last week.",
            GeneratedAtUtc = MondayMorning.AddDays(-7),
            PromptVersion = TrendInterpretationService.CurrentPromptVersion,
        });

        Assert.Equal(1, await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning));
    }

    [Fact]
    public async Task ANarrativeFromAnOlderBriefIsRewrittenTheSameDay()
    {
        // The version gate beats the once-a-day gate, so a deployed prompt fix reaches every
        // member on their next due pass rather than waiting a week for one.
        _insights.GetByScopeAsync(_memberId, InsightScope.TrendWeekly).Returns(new MemberInsight
        {
            CardiMemberId = _memberId,
            Scope = InsightScope.TrendWeekly,
            Summary = "Written by an older brief.",
            GeneratedAtUtc = MondayMorning.AddHours(-1),
            PromptVersion = TrendInterpretationService.CurrentPromptVersion - 1,
        });

        Assert.Equal(1, await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning));
    }

    [Fact]
    public async Task AWeekMeasuredOnThreeDaysIsNotNarrated()
    {
        // The Weekbook's own four-of-seven bar. The member has a full quarter of history, so this
        // is not the cold start — it is a week nobody measured, and an account of it would have to
        // speak for the days that are missing.
        WithPeriodCoverage(measuredDays: 3);

        Assert.Equal(0, await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning));
        Assert.Empty(_medicalAi.ReceivedCalls());
    }

    [Fact]
    public async Task AWeekMeasuredOnFourDaysIsNarrated()
    {
        WithPeriodCoverage(measuredDays: 4);

        Assert.Equal(1, await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning));
    }

    [Fact]
    public async Task AMemberUnderAMonthOfHistoryIsStillTheLearningState()
    {
        // The horizon changes what is described, never how much history it takes before anything
        // is. Three weeks of readings earns no narrative at any horizon.
        WithHistory(20);

        Assert.Equal(0, await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning));
    }

    [Fact]
    public async Task APausedMemberIsNotNarrated()
    {
        var paused = Member();
        paused.MonitoringPausedUntil = MondayMorning.AddDays(3);
        _members.GetByIdAsync(_memberId).Returns(paused);

        Assert.Equal(0, await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning));
    }

    [Fact]
    public async Task TheWeeklyBriefSaysItIsReadingAWeek()
    {
        await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning);

        var prompt = CapturedPrompt();
        Assert.Contains("the week that has just ended", prompt);
        // And the shared body is still all there — the horizons change the opening, nothing else.
        Assert.Contains("Never give a score, a probability", prompt);
        Assert.Contains("Never name a condition", prompt);
        Assert.Contains("Pinned reference ranges", prompt);
    }

    [Fact]
    public async Task TheMonthlyBriefSaysItIsReadingAMonth()
    {
        await CreateSut().InterpretDueJournalHorizonsAsync(FirstOfMonth);

        Assert.Contains("the month that has just ended", CapturedPrompt());
    }

    [Fact]
    public async Task ANarrativeThatNamesAConditionIsNotStored()
    {
        // The same register guard the rolling pass applies. There is no rewrite slot on this path
        // to soften one, so a reply that names a condition is refused whole.
        WithReply("This looks like sleep apnoea.", ["Worth a look."]);

        Assert.Equal(0, await CreateSut().InterpretDueJournalHorizonsAsync(MondayMorning));
        Assert.DoesNotContain(
            _insights.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(IMemberInsightRepository.AddAsync));
    }

    private CardiMember Member(TimeOnly? weekbookTime = null) => new()
    {
        Id = _memberId,
        Name = "Margaret Doe",
        DateOfBirth = new DateOnly(1948, 3, 15),
        IsActive = true,
        WeekbookLocalTime = weekbookTime,
    };

    private TrendInterpretationService CreateSut() =>
        new(_unitOfWork, _medicalAi, PromptContextFactory.Composer(_unitOfWork),
            NullLogger<TrendInterpretationService>.Instance);

    /// <summary>A full quarter of readings, every day measured — the default for these tests.</summary>
    private void WithFullHistory() => WithHistory(90);

    private void WithHistory(int days)
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(
                _memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(call => LogsEndingAt((DateOnly)call[2], days));
    }

    /// <summary>
    /// A full history, but only <paramref name="measuredDays"/> of the period itself measured.
    /// The two reads are told apart by their span: the period read is at most thirty-one days
    /// wide, the history read is ninety.
    /// </summary>
    private void WithPeriodCoverage(int measuredDays)
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(
                _memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(call =>
            {
                var from = (DateOnly)call[1];
                var through = (DateOnly)call[2];
                var span = through.DayNumber - from.DayNumber + 1;
                return span > 40 ? LogsEndingAt(through, 90) : LogsEndingAt(through, measuredDays);
            });
    }

    private List<ActivityLog> LogsEndingAt(DateOnly through, int days) =>
        Enumerable.Range(0, days)
            .Select(offset => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = through.AddDays(-(days - 1 - offset)),
                Steps = 4000,
                RestingHeartRate = 62,
            })
            .ToList();

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
