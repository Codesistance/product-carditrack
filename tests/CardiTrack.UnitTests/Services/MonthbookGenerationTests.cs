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
/// Pins when a Monthbook is written and when it is refused: on the first of the month, after the
/// member's own hour, once per month, never from a month too thin to account for, and never from
/// the month's Weekbooks.
/// </summary>
public class MonthbookGenerationTests
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

    /// <summary>1 August 2026, 09:30 UTC — 10:30 in London, past the 02:00 default.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>The month that ended the night before: July 2026.</summary>
    private static readonly DateOnly MonthStart = new(2026, 7, 1);
    private static readonly DateOnly MonthEnd = new(2026, 7, 31);

    public MonthbookGenerationTests()
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

        SetupMonth(daysWithData: 31);

        _digests.GetLatestByDateAsync(
                _memberId, Arg.Any<DateOnly>(), DigestAudience.Monthbook, Arg.Any<CancellationToken>())
            .Returns((DigestEntry?)null);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);
        _alerts.GetByCardiMemberAsync(_memberId, Arg.Any<bool>()).Returns([]);
        _realtimeAssessments.GetBetweenAsync(
                _memberId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>()).Returns([]);

        SetupModelReply("A steady month for sleep", "Ada's July held together well.");
    }

    private CardiMember Member() => new()
    {
        Id = _memberId,
        Name = "Ada Doe",
        DateOfBirth = new DateOnly(1948, 3, 2),
        Gender = Gender.Female,
        IsActive = true,
    };

    private void SetupMonth(int daysWithData)
    {
        var logs = Enumerable.Range(0, daysWithData)
            .Select(i => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = MonthStart.AddDays(i),
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
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.MonthbookAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.MonthbookAiResponse
            {
                Headline = headline,
                Summary = summary,
            });

    private DigestGenerationService CreateSut() =>
        new(_unitOfWork, _medicalAi, PromptContextFactory.Composer(_unitOfWork),
            PromptContextFactory.Encryption, InertStatusLineGenerator.Create(),
            InertAdviseGenerator.Create(), NullLogger<DigestGenerationService>.Instance);

    private async Task AssertNothingWritten()
    {
        Assert.Equal(0, await CreateSut().GenerateDueMonthbooksAsync(UtcNow));
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    // ── Due-ness ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Writes_one_monthbook_dated_by_the_months_last_day()
    {
        var generated = await CreateSut().GenerateDueMonthbooksAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d =>
                d.CardiMemberId == _memberId &&
                d.Audience == DigestAudience.Monthbook &&
                d.LocalDate == MonthEnd &&
                d.Headline == "A steady month for sleep" &&
                d.GeneratedAtUtc == UtcNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Not_due_on_any_day_but_the_first()
    {
        var midMonth = new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Utc);

        Assert.Equal(0, await CreateSut().GenerateDueMonthbooksAsync(midMonth));
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.MonthbookAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Mid-month no timezone can be on the first, so the pass answers without reading anything —
    /// it runs 48 times a day and would otherwise fetch the candidate list, then a row and a
    /// timezone per candidate, only to decline every one on a date comparison.
    /// </summary>
    [Fact]
    public async Task Mid_month_the_pass_reads_nothing_at_all()
    {
        var midMonth = new DateTime(2026, 8, 15, 9, 30, 0, DateTimeKind.Utc);

        Assert.Equal(0, await CreateSut().GenerateDueMonthbooksAsync(midMonth));

        await _members.DidNotReceive().GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>());
        await _members.DidNotReceive().GetByIdAsync(Arg.Any<Guid>());
    }

    /// <summary>
    /// The guard is generous on purpose. Late on the 31st in UTC it is already the 1st east of the
    /// line, and early on the 1st in UTC it is still the 31st west of it — a member is due on both
    /// occasions, and a guard that trusted the UTC date alone would lose them their book for good.
    /// </summary>
    [Theory]
    [InlineData(2026, 7, 31, 23, 0)]  // UTC still July; UTC+14 is already 1 August
    [InlineData(2026, 8, 1, 0, 30)]   // UTC already August; UTC-12 is still 31 July
    [InlineData(2026, 8, 1, 23, 30)]  // UTC still 1 August
    public void The_offset_span_guard_admits_every_instant_some_timezone_is_on_the_first(
        int year, int month, int day, int hour, int minute)
    {
        var utc = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);

        Assert.True(DigestGenerationService.AnyTimeZoneCouldBeOnDayOfMonth(utc, 1));
    }

    [Theory]
    [InlineData(2026, 8, 15, 12, 0)]
    [InlineData(2026, 8, 3, 12, 0)]   // two days past the 1st, outside the 26-hour span
    [InlineData(2026, 8, 29, 0, 0)]   // three days short of the 1st
    public void The_guard_refuses_instants_no_timezone_could_be_on_the_first(
        int year, int month, int day, int hour, int minute)
    {
        var utc = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);

        Assert.False(DigestGenerationService.AnyTimeZoneCouldBeOnDayOfMonth(utc, 1));
    }

    [Fact]
    public async Task Not_due_before_the_members_chosen_hour()
    {
        var member = Member();
        member.MonthbookLocalTime = new TimeOnly(12, 0); // local clock is 10:30
        _members.GetByIdAsync(_memberId).Returns(member);

        await AssertNothingWritten();
    }

    [Fact]
    public async Task Due_once_the_members_chosen_hour_has_passed()
    {
        var member = Member();
        member.MonthbookLocalTime = new TimeOnly(9, 0);
        _members.GetByIdAsync(_memberId).Returns(member);

        Assert.Equal(1, await CreateSut().GenerateDueMonthbooksAsync(UtcNow));
    }

    [Fact]
    public async Task Never_written_twice_for_the_same_month()
    {
        _digests.GetLatestByDateAsync(
                _memberId, MonthEnd, DigestAudience.Monthbook, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry { CardiMemberId = _memberId, LocalDate = MonthEnd, Text = "already" });

        await AssertNothingWritten();
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.MonthbookAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_paused_member_gets_no_monthbook()
    {
        var member = Member();
        member.MonitoringPausedUntil = UtcNow.AddDays(1);
        _members.GetByIdAsync(_memberId).Returns(member);

        await AssertNothingWritten();
    }

    // ── The data guard ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(13)]
    public async Task A_month_too_thin_to_account_for_is_skipped(int daysWithData)
    {
        SetupMonth(daysWithData);

        await AssertNothingWritten();
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.MonthbookAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fourteen_measured_days_is_enough()
    {
        SetupMonth(daysWithData: 14);

        Assert.Equal(1, await CreateSut().GenerateDueMonthbooksAsync(UtcNow));
    }

    // ── Independence from the Weekbooks ─────────────────────────────────────

    /// <summary>
    /// The month is built from measurements, so neither the Weekbook nor the Daybook series is
    /// consulted — a month whose Weekbooks were discarded still gets its Monthbook.
    /// </summary>
    [Fact]
    public async Task The_months_weekbooks_and_daybooks_are_never_read()
    {
        await CreateSut().GenerateDueMonthbooksAsync(UtcNow);

        await _digests.DidNotReceive().GetLatestByDateAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), DigestAudience.Weekbook, Arg.Any<CancellationToken>());
        await _digests.DidNotReceive().GetLatestByDateAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), DigestAudience.Daybook, Arg.Any<CancellationToken>());
    }

    // ── The guards ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_naming_a_condition_is_discarded()
    {
        SetupModelReply("A concerning month", "Her readings this month were a sign of atrial fibrillation.");

        await AssertNothingWritten();
    }

    [Fact]
    public async Task A_reply_that_restates_its_own_brief_is_discarded()
    {
        SetupModelReply("A month", "Say what held across the whole month, and what changed within it.");

        await AssertNothingWritten();
    }

    [Fact]
    public async Task An_empty_reply_is_discarded()
    {
        SetupModelReply("A month", "   ");

        await AssertNothingWritten();
    }

    // ── Defaults ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unset_member_uses_the_journal_default_time()
    {
        var member = Member();
        Assert.Null(member.MonthbookLocalTime);
        Assert.Equal(new TimeOnly(2, 0), JournalSchedule.EffectiveTime(member.MonthbookLocalTime));

        Assert.Equal(1, await CreateSut().GenerateDueMonthbooksAsync(UtcNow));
    }
}
