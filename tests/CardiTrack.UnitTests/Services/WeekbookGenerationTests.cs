using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Common;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins when a Weekbook is written and when it is refused: on the member's own week-start day,
/// after their own chosen hour, once per week, never from a week too thin to account for, and
/// never from the week's Daybooks.
/// </summary>
public class WeekbookGenerationTests
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
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();

    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    /// <summary>Monday 10 August 2026, 09:30 UTC — 10:30 in London, past the 02:00 default.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 10, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>The week that ended the night before: Monday 3rd to Sunday 9th.</summary>
    private static readonly DateOnly WeekStart = new(2026, 8, 3);
    private static readonly DateOnly WeekEnd = new(2026, 8, 9);

    public WeekbookGenerationTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.Users.Returns(_users);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.Digests.Returns(_digests);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.RealtimeAssessments.Returns(_realtimeAssessments);
        _unitOfWork.MemberQuestionnaires.Returns(_questionnaires);

        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());

        _links.GetByCardiMemberIdAsync(_memberId).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = _memberId, IsActive = true },
        ]);
        _users.GetByIdAsync(_userId).Returns(new User { Id = _userId, TimeZoneId = "Europe/London" });

        SetupWeek(daysWithData: 7);

        _digests.GetLatestByDateAsync(
                _memberId, Arg.Any<DateOnly>(), DigestAudience.Weekbook, Arg.Any<CancellationToken>())
            .Returns((DigestEntry?)null);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);
        _alerts.GetByCardiMemberAsync(_memberId, Arg.Any<bool>()).Returns([]);
        _realtimeAssessments.GetBetweenAsync(
                _memberId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>()).Returns([]);

        SetupModelReply("A steadier week for sleep", "Ada slept a little more than usual this week.");
    }

    private CardiMember Member() => new()
    {
        Id = _memberId,
        Name = "Ada Doe",
        DateOfBirth = new DateOnly(1948, 3, 2),
        Gender = Gender.Female,
        IsActive = true,
    };

    private void SetupWeek(int daysWithData)
    {
        var logs = Enumerable.Range(0, daysWithData)
            .Select(i => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = WeekStart.AddDays(i),
                Steps = 5000,
                RestingHeartRate = 64,
                SleepMinutes = 430,
            })
            .ToList();

        _activityLogs.GetByCardiMemberAndDateRangeAsync(
                _memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(logs);
    }

    private void SetupModelReply(string headline, string summary) =>
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.WeekbookAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.WeekbookAiResponse
            {
                Headline = headline,
                Summary = summary,
            });

    private DigestGenerationService CreateSut() =>
        new(_unitOfWork, _medicalAi, Substitute.For<IRewriteAiService>(),
            PromptContextFactory.Composer(_unitOfWork),
            PromptContextFactory.Encryption, InertStatusLineGenerator.Create(),
            InertAdviseGenerator.Create(), NullLogger<DigestGenerationService>.Instance);

    private Task<int> NoWeekbookWritten() => CreateSut().GenerateDueWeekbooksAsync(UtcNow);

    private async Task AssertNothingWritten()
    {
        Assert.Equal(0, await NoWeekbookWritten());
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    // ── Due-ness ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Writes_one_weekbook_dated_by_the_weeks_last_day()
    {
        var generated = await CreateSut().GenerateDueWeekbooksAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d =>
                d.CardiMemberId == _memberId &&
                d.Audience == DigestAudience.Weekbook &&
                d.LocalDate == WeekEnd &&
                d.Headline == "A steadier week for sleep" &&
                d.GeneratedAtUtc == UtcNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Not_due_on_a_day_that_is_not_the_members_week_start()
    {
        var member = Member();
        member.JournalWeekStartsOn = DayOfWeek.Tuesday;
        _members.GetByIdAsync(_memberId).Returns(member);

        await AssertNothingWritten();
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.WeekbookAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Honours_a_members_own_week_start()
    {
        // Sunday-start member: at Monday 10:30 local their week turned yesterday, not today.
        var member = Member();
        member.JournalWeekStartsOn = DayOfWeek.Sunday;
        _members.GetByIdAsync(_memberId).Returns(member);

        await AssertNothingWritten();
    }

    [Fact]
    public async Task Not_due_before_the_members_chosen_hour()
    {
        var member = Member();
        member.WeekbookLocalTime = new TimeOnly(12, 0); // local clock is 10:30
        _members.GetByIdAsync(_memberId).Returns(member);

        await AssertNothingWritten();
    }

    [Fact]
    public async Task Due_once_the_members_chosen_hour_has_passed()
    {
        var member = Member();
        member.WeekbookLocalTime = new TimeOnly(9, 0); // local clock is 10:30
        _members.GetByIdAsync(_memberId).Returns(member);

        Assert.Equal(1, await CreateSut().GenerateDueWeekbooksAsync(UtcNow));
    }

    [Fact]
    public async Task Never_written_twice_for_the_same_week()
    {
        _digests.GetLatestByDateAsync(
                _memberId, WeekEnd, DigestAudience.Weekbook, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry { CardiMemberId = _memberId, LocalDate = WeekEnd, Text = "already" });

        await AssertNothingWritten();
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.WeekbookAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_paused_member_gets_no_weekbook()
    {
        var member = Member();
        member.MonitoringPausedUntil = UtcNow.AddDays(1);
        _members.GetByIdAsync(_memberId).Returns(member);

        await AssertNothingWritten();
    }

    // ── The data guard ──────────────────────────────────────────────────────

    /// <summary>Silence must never read as healthy: an unmeasured week gets no account at all.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_week_too_thin_to_account_for_is_skipped(int daysWithData)
    {
        SetupWeek(daysWithData);

        await AssertNothingWritten();
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.WeekbookAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Four_measured_days_is_enough()
    {
        SetupWeek(daysWithData: 4);

        Assert.Equal(1, await CreateSut().GenerateDueWeekbooksAsync(UtcNow));
    }

    // ── Independence from the Daybooks ──────────────────────────────────────

    /// <summary>
    /// The whole point of a book reading its own period: the week is built from measurements, so
    /// the Daybook series is never consulted and a week whose Daybooks were discarded still gets
    /// its Weekbook.
    /// </summary>
    [Fact]
    public async Task The_weeks_daybooks_are_never_read()
    {
        await CreateSut().GenerateDueWeekbooksAsync(UtcNow);

        await _digests.DidNotReceive().GetLatestByDateAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), DigestAudience.Daybook, Arg.Any<CancellationToken>());
        await _digests.DidNotReceive().GetHistoryAsync(
            Arg.Any<Guid>(), DigestAudience.Daybook, Arg.Any<int>(), Arg.Any<string?>(),
            Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<DigestUrgency?>(),
            Arg.Any<CancellationToken>());
    }

    // ── The guards ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_naming_a_condition_is_discarded()
    {
        SetupModelReply("A concerning week", "Her readings this week were a sign of atrial fibrillation.");

        await AssertNothingWritten();
    }

    [Fact]
    public async Task A_reply_that_restates_its_own_brief_is_discarded()
    {
        SetupModelReply("A week", "A list of seven days is not an account of a week.");

        await AssertNothingWritten();
    }

    [Fact]
    public async Task A_reply_using_a_precise_term_without_explaining_it_is_discarded()
    {
        SetupModelReply("A steady week", "Her sleep efficiency held steady across the whole week.");

        await AssertNothingWritten();
    }

    [Fact]
    public async Task An_empty_reply_is_discarded()
    {
        SetupModelReply("A week", "   ");

        await AssertNothingWritten();
    }

    // ── Defaults ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unset_member_uses_the_journal_defaults()
    {
        var member = Member();
        Assert.Null(member.JournalWeekStartsOn);
        Assert.Null(member.WeekbookLocalTime);

        // Monday start, 02:00 — both defaults, and both satisfied at Monday 10:30 local.
        Assert.Equal(DayOfWeek.Monday, JournalSchedule.EffectiveWeekStart(member.JournalWeekStartsOn));
        Assert.Equal(new TimeOnly(2, 0), JournalSchedule.EffectiveTime(member.WeekbookLocalTime));
        Assert.Equal(1, await CreateSut().GenerateDueWeekbooksAsync(UtcNow));
    }
}
