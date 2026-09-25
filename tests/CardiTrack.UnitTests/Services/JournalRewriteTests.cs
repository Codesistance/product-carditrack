using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using CardiTrack.UnitTests.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins <see cref="DigestGenerationService.ComposeBookAsync"/> — the composition behind the one way
/// a CardiJournal book changes after it is written. Driven mostly through the Weekbook: the book
/// is composed and guarded but never stored here, and every refusal the scheduled pass makes is
/// made here too.
/// </summary>
[Collection(JournalTelemetryCollection.Name)]
public class JournalRewriteTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IDigestRepository _digests = Substitute.For<IDigestRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IRealtimeAssessmentRepository _realtimeAssessments =
        Substitute.For<IRealtimeAssessmentRepository>();
    private readonly IMemberQuestionnaireRepository _questionnaires =
        Substitute.For<IMemberQuestionnaireRepository>();
    private readonly IGranularMetricRepository _granular = Substitute.For<IGranularMetricRepository>();
    private readonly IDeviceActivityLogRepository _deviceLogs = Substitute.For<IDeviceActivityLogRepository>();
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();

    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    /// <summary>Wednesday 12 August 2026, 09:30 UTC — 10:30 in London. Not the member's week
    /// start, so the due pass would write nothing today; a caregiver's ask does not wait for it.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 12, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>The finished week being rewritten: Monday 3rd to Sunday 9th.</summary>
    private static readonly DateOnly WeekStart = new(2026, 8, 3);
    private static readonly DateOnly WeekEnd = new(2026, 8, 9);

    public JournalRewriteTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        GenerationLeaseStub.GrantAll(_unitOfWork);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.Users.Returns(_users);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.Digests.Returns(_digests);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.RealtimeAssessments.Returns(_realtimeAssessments);
        _unitOfWork.MemberQuestionnaires.Returns(_questionnaires);
        _unitOfWork.GranularMetrics.Returns(_granular);
        _unitOfWork.DeviceActivityLogs.Returns(_deviceLogs);

        // The Daybook's intraday reads: nothing granular and no device log, so the day is written
        // from its daily rollup alone — the shape a member without granular ingestion has.
        _granular.GetRollupsAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _deviceLogs.GetByCardiMemberAndDateAsync(_memberId, Arg.Any<DateOnly>()).Returns([]);

        _members.GetByIdAsync(_memberId).Returns(Member());
        _links.GetByCardiMemberIdAsync(_memberId).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = _memberId, IsActive = true },
        ]);
        _users.GetByIdAsync(_userId).Returns(new User { Id = _userId, TimeZoneId = "Europe/London" });

        SetupWeek(daysWithData: 7);

        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);
        _alerts.GetByCardiMemberAsync(_memberId, Arg.Any<bool>()).Returns([]);
        _realtimeAssessments.GetBetweenAsync(
                _memberId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>()).Returns([]);

        SetupModelReply(
            "A steadier week for sleep",
            "Ada slept a little more than usual this week. Her resting heart rate held at her usual. "
            + "Steps were lower on Thursday than on the other six days.");
    }

    private CardiMember Member() => new()
    {
        Id = _memberId,
        FirstName = "Ada",
        LastName = "Doe",
        DateOfBirth = new DateOnly(1948, 3, 2),
        Gender = Gender.Female,
        IsActive = true,
    };

    private void SetupWeek(int daysWithData) => SetupDays(WeekStart, daysWithData);

    private void SetupDays(DateOnly from, int daysWithData)
    {
        var logs = Enumerable.Range(0, daysWithData)
            .Select(i => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = from.AddDays(i),
                Steps = 5000,
                RestingHeartRate = 64,
                SleepMinutes = 430,
            })
            .ToList();

        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(logs);
    }

    private void SetupModelReply(string headline, string summary)
    {
        // The clinical half reads; the rewrite half writes what a family reads, echoing the
        // read back so these tests still assert on the text they always did.
        JournalRewriteEcho.Wire(_rewriteAi, headline);
        _medicalAi.GenerateStructuredWithUsageAsync<DigestGenerationService.WeekbookAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<DigestGenerationService.WeekbookAiResponse>(
                new DigestGenerationService.WeekbookAiResponse
                {
                    Finding = summary,
                    Urgency = "watch",
                },
                new AiUsage { ModelName = "test-medical", InputTokens = 900, OutputTokens = 120 }));
    }

    private DigestGenerationService CreateSut() =>
        new(_unitOfWork, _medicalAi, _rewriteAi,
            PromptContextFactory.Composer(_unitOfWork),
            PromptContextFactory.Encryption, InertStatusLineGenerator.Create(),
            InertAdviseGenerator.Create(), NullLogger<DigestGenerationService>.Instance, new PassThroughWriteGuard());

    private Task<JournalRewriteResult> Compose(DateOnly periodEnd, DigestAudience audience = DigestAudience.Weekbook) =>
        CreateSut().ComposeBookAsync(_memberId, audience, periodEnd, UtcNow);

    private async Task AssertNothingChanged()
    {
        await _digests.DidNotReceive().ReplaceBookAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
        await _digests.DidNotReceiveWithAnyArgs().DeleteBookAsync(default, default, default, default);
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    // ── The write ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Composes_the_book_for_the_named_week_on_any_day()
    {
        var result = await Compose(WeekEnd);

        Assert.Equal(JournalRewriteOutcome.Written, result.Outcome);
        Assert.Equal("test-medical", result.Usage?.ModelName);
        Assert.NotNull(result.Entry);
        Assert.Equal(_memberId, result.Entry.CardiMemberId);
        Assert.Equal(DigestAudience.Weekbook, result.Entry.Audience);
        Assert.Equal(WeekEnd, result.Entry.LocalDate);
        Assert.Equal(UtcNow, result.Entry.GeneratedAtUtc);
        Assert.Equal("A steadier week for sleep", result.Entry.Headline);
    }

    /// <summary>
    /// Composing stores nothing: the caller stores, inside whatever transaction its own record of
    /// the request needs, and a model call therefore never runs inside one.
    /// </summary>
    [Fact]
    public async Task A_composition_stores_nothing_itself()
    {
        var result = await Compose(WeekEnd);

        Assert.Equal(JournalRewriteOutcome.Written, result.Outcome);
        Assert.False(result.ReplacedAnEarlierBook);
        await AssertNothingChanged();
    }

    // ── The refusals ────────────────────────────────────────────────────────

    /// <summary>
    /// A reply the guards refuse changes nothing: the caregiver asked for a better account, and
    /// the honest answer is that there is not one yet — not that the old one is gone.
    /// </summary>
    [Fact]
    public async Task A_refused_reply_leaves_the_existing_book_in_place()
    {
        SetupModelReply("A concerning week", "Her readings this week were a sign of atrial fibrillation.");

        var result = await Compose(WeekEnd);

        Assert.Equal(JournalRewriteOutcome.Discarded, result.Outcome);
        Assert.NotNull(result.Usage);
        await AssertNothingChanged();
    }

    [Fact]
    public async Task A_week_still_in_progress_is_refused_before_any_model_call()
    {
        var result = await Compose(new DateOnly(2026, 8, 16));

        Assert.Equal(JournalRewriteOutcome.PeriodNotFinished, result.Outcome);
        await _medicalAi.DidNotReceiveWithAnyArgs()
            .GenerateStructuredWithUsageAsync<DigestGenerationService.WeekbookAiResponse>(default!, default);
        await AssertNothingChanged();
    }

    /// <summary>Today in the member's own clock, not UTC's: the period must have ended for them.</summary>
    [Fact]
    public async Task A_week_ending_today_is_not_finished()
    {
        var result = await Compose(new DateOnly(2026, 8, 12));

        Assert.Equal(JournalRewriteOutcome.PeriodNotFinished, result.Outcome);
    }

    [Fact]
    public async Task A_paused_member_is_not_written_about()
    {
        var member = Member();
        member.MonitoringPausedUntil = UtcNow.AddDays(1);
        _members.GetByIdAsync(_memberId).Returns(member);

        var result = await Compose(WeekEnd);

        Assert.Equal(JournalRewriteOutcome.MemberUnavailable, result.Outcome);
        await AssertNothingChanged();
    }

    [Fact]
    public async Task A_week_too_thin_to_account_for_says_how_thin()
    {
        SetupWeek(daysWithData: 2);

        var result = await Compose(WeekEnd);

        Assert.Equal(JournalRewriteOutcome.NoReadings, result.Outcome);
        Assert.Equal(2, result.DaysWithData);
        Assert.Equal(4, result.DaysNeeded);
        await AssertNothingChanged();
    }

    /// <summary>
    /// The first book, at the service level: a finished day composed from its own daily row and
    /// intraday reads — not only the scheduled path's due check.
    /// </summary>
    [Fact]
    public async Task Composes_a_daybook_for_a_finished_day()
    {
        string? prompt = null;
        JournalRewriteEcho.Wire(_rewriteAi, "A settled Sunday");
        _medicalAi.GenerateStructuredWithUsageAsync<DigestGenerationService.DaybookAiResponse>(
                Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<DigestGenerationService.DaybookAiResponse>(
                new DigestGenerationService.DaybookAiResponse
                {
                    Finding = "Ada slept close to her usual and was up and about by mid-morning. Her resting "
                        + "heart rate held steady through the day.",
                    Urgency = "watch",
                },
                new AiUsage { ModelName = "test-medical" }));

        var result = await Compose(WeekEnd, DigestAudience.Daybook);

        Assert.Equal(JournalRewriteOutcome.Written, result.Outcome);
        Assert.NotNull(result.Entry);
        Assert.Equal(WeekEnd, result.Entry.LocalDate);
        Assert.Equal(DigestAudience.Daybook, result.Entry.Audience);
        Assert.Equal("A settled Sunday", result.Entry.Headline);
        Assert.NotNull(prompt);
        Assert.Contains("[PATIENT CONTEXT]", prompt);
        Assert.Contains("[INPUT DATA]", prompt);
        Assert.Contains("\"sleep_duration_hours\":", prompt);
        Assert.Contains("\"resting_heart_rate\":", prompt);
        Assert.EndsWith("JSON:", prompt.TrimEnd());
        await AssertNothingChanged();
    }

    /// <summary>A day with no daily row is unmeasured, not quiet: nothing to write from, nothing touched.</summary>
    [Fact]
    public async Task A_day_without_readings_has_nothing_to_write_from()
    {
        var result = await Compose(new DateOnly(2026, 8, 11), DigestAudience.Daybook);

        Assert.Equal(JournalRewriteOutcome.NoReadings, result.Outcome);
        await _medicalAi.DidNotReceiveWithAnyArgs()
            .GenerateStructuredWithUsageAsync<DigestGenerationService.DaybookAiResponse>(default!, default);
        await AssertNothingChanged();
    }

    /// <summary>
    /// The third book goes through the same composition: a finished month, dated by its last day,
    /// composed from the month's own days.
    /// </summary>
    [Fact]
    public async Task Composes_a_monthbook_for_a_finished_month()
    {
        var monthStart = new DateOnly(2026, 7, 1);
        var monthEnd = new DateOnly(2026, 7, 31);
        SetupDays(monthStart, daysWithData: 20);
        JournalRewriteEcho.Wire(_rewriteAi, "A steady month for sleep");
        _medicalAi.GenerateStructuredWithUsageAsync<DigestGenerationService.MonthbookAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<DigestGenerationService.MonthbookAiResponse>(
                new DigestGenerationService.MonthbookAiResponse
                {
                    Finding = "Ada's July held together well. Sleep ran a little longer than her usual across "
                        + "all four weeks. The week of 20 July was the quietest for steps.",
                    Urgency = "watch",
                },
                new AiUsage { ModelName = "test-medical" }));

        var result = await Compose(monthEnd, DigestAudience.Monthbook);

        Assert.Equal(JournalRewriteOutcome.Written, result.Outcome);
        Assert.NotNull(result.Entry);
        Assert.Equal(monthEnd, result.Entry.LocalDate);
        Assert.Equal(DigestAudience.Monthbook, result.Entry.Audience);
        Assert.Equal("A steady month for sleep", result.Entry.Headline);
    }

    [Fact]
    public async Task A_month_too_thin_to_account_for_says_how_thin()
    {
        SetupDays(new DateOnly(2026, 7, 1), daysWithData: 10);

        var result = await Compose(new DateOnly(2026, 7, 31), DigestAudience.Monthbook);

        Assert.Equal(JournalRewriteOutcome.NoReadings, result.Outcome);
        Assert.Equal(10, result.DaysWithData);
        Assert.Equal(14, result.DaysNeeded);
        await AssertNothingChanged();
    }

    [Fact]
    public async Task The_family_series_cannot_be_rewritten()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Compose(WeekEnd, DigestAudience.Family));
    }

    /// <summary>
    /// The due pass lost nothing in the split: a Monday-start member at 10:30 on Monday still gets
    /// the week just gone, stored by the due pass itself from the same composition.
    /// </summary>
    [Fact]
    public async Task The_scheduled_pass_still_writes_through_the_same_composition()
    {
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _digests.GetLatestByDateAsync(_memberId, Arg.Any<DateOnly>(), DigestAudience.Weekbook, Arg.Any<CancellationToken>())
            .Returns((DigestEntry?)null);
        // The pass counts a book as written only when the insert says it stored a row.
        _digests.AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>()).Returns(true);
        var monday = new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Utc);

        var generated = await CreateSut().GenerateDueWeekbooksAsync(monday);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
        await _digests.DidNotReceive().ReplaceBookAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }
}
