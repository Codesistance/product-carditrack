using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Common;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Diagnostics;
using CardiTrack.Infrastructure.Services;
using CardiTrack.UnitTests.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins when a Weekbook is written and when it is refused: on the member's own week-start day,
/// after their own chosen hour, once per week, never from a week too thin to account for, and
/// never from the week's Daybooks. Also pins what the pass says about each of those
/// decisions, on the outcome counter every journal pass records to.
/// </summary>
[Collection(JournalTelemetryCollection.Name)]
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
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();

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
        GenerationLeaseStub.GrantAll(_unitOfWork);
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
        // The insert says whether it stored a row, and the pass counts on the answer: an
        // unstubbed false would read as "another execution won the index" on every test.
        _digests.AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>()).Returns(true);
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
                new AiUsage { ModelName = "test-medical" }));
    }

    private DigestGenerationService CreateSut() =>
        new(_unitOfWork, _medicalAi, _rewriteAi,
            PromptContextFactory.Composer(_unitOfWork),
            PromptContextFactory.Encryption, InertStatusLineGenerator.Create(),
            InertAdviseGenerator.Create(), NullLogger<DigestGenerationService>.Instance, new PassThroughWriteGuard());

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
        await _medicalAi.DidNotReceive().GenerateStructuredWithUsageAsync<DigestGenerationService.WeekbookAiResponse>(
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
        await _medicalAi.DidNotReceive().GenerateStructuredWithUsageAsync<DigestGenerationService.WeekbookAiResponse>(
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
        await _medicalAi.DidNotReceive().GenerateStructuredWithUsageAsync<DigestGenerationService.WeekbookAiResponse>(
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

    /// <summary>
    /// A bare term no longer costs the week: it is explained in code where it is first used, and
    /// the reply is written with the explanation in. The discard it replaced was the mechanism
    /// that left a one-line "unremarkable week" as the only reply to survive a day of retries.
    /// </summary>
    [Fact]
    public async Task A_reply_using_a_precise_term_without_explaining_it_is_glossed_and_written()
    {
        SetupModelReply(
            "A steady week",
            "Her sleep efficiency held steady across the whole week. Her resting heart rate sat at her usual. "
            + "Steps were lower than usual on four of the seven days.");

        Assert.Equal(1, await CreateSut().GenerateDueWeekbooksAsync(UtcNow));

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d =>
                d.Text.StartsWith("Her sleep efficiency (the share of time in bed actually spent asleep) held steady")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The gloss lengthens a reply the model had already finished, so a reply near the column's
    /// cap can be pushed over it — refused here, not by the database on the insert.
    /// </summary>
    [Fact]
    public async Task A_reply_the_gloss_pushes_over_the_text_cap_is_discarded()
    {
        // 3,986 characters as the model wrote it — under the cap until the gloss adds its 49.
        var filler = new string('x', DigestEntry.MaxTextLength - 90);
        SetupModelReply(
            "A steady week",
            $"Her sleep efficiency held steady. Her resting heart rate sat at her usual. {filler}.");

        await AssertNothingWritten();
    }

    /// <summary>
    /// One sentence is not an account of a week, whatever it says: the brief asks for six or
    /// more, and the single-line reply was the shape the old discard loop selected for.
    /// </summary>
    [Fact]
    public async Task A_reply_too_short_to_be_an_account_is_discarded()
    {
        SetupModelReply("An unremarkable week", "Ada had an unremarkable week, with every reading in its usual range.");

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

    /// <summary>
    /// The whole read crosses to the Rewrite slot, not the first thousand characters of it.
    /// </summary>
    /// <remarks>
    /// The split first shipped using <c>MedicalPromptBlocks.Flatten</c> to prepare the read for
    /// redaction. That is the right helper for a caregiver note and the wrong one for a book: its
    /// cap is 1,000 characters, so a week's account arrived at the rewrite truncated to its first
    /// third with "… (truncated)" on the end, and the rewrite wrote a confident account of a week
    /// it had only been shown the start of. Nothing about the output looked wrong — which is why
    /// this is pinned rather than left to the length guard that happened to catch it.
    /// </remarks>
    [Fact]
    public async Task The_whole_clinical_read_reaches_the_rewrite()
    {
        var longAccount = "Her sleep held close to her usual. " + new string('x', 2_500) + ".";
        SetupModelReply("A steady week", longAccount);

        await CreateSut().GenerateDueWeekbooksAsync(UtcNow);

        var prompt = _rewriteAi.ReceivedCalls()
            .Select(c => c.GetArguments()[0] as string)
            .Last(arg => arg is not null && arg.Contains("Clinical read to write from", StringComparison.Ordinal))!;

        Assert.DoesNotContain("(truncated)", prompt, StringComparison.Ordinal);
        Assert.Contains(new string('x', 2_500), prompt, StringComparison.Ordinal);
    }

    // ── What the pass says about each decision ───────────────────────────────

    /// <summary>
    /// Every exit from the per-member path lands on the outcome counter, tagged with the book
    /// and the reason. These were all bare <c>return false</c>s once, and a pass that declined
    /// every member left nothing behind to say why — or that it had run at all.
    /// </summary>
    [Fact]
    public async Task A_written_weekbook_counts_as_written()
    {
        using var capture = new JournalMetricCapture();

        await CreateSut().GenerateDueWeekbooksAsync(UtcNow);

        Assert.Contains(("weekbook", "written"), capture.Outcomes);
    }

    [Fact]
    public async Task A_member_declined_on_the_weekday_counts_as_not_due()
    {
        using var capture = new JournalMetricCapture();

        // Tuesday 11 August, same hour: not the member's week start.
        await CreateSut().GenerateDueWeekbooksAsync(UtcNow.AddDays(1));

        Assert.Contains(("weekbook", "not_due"), capture.Outcomes);
        Assert.DoesNotContain(("weekbook", "written"), capture.Outcomes);
    }

    [Fact]
    public async Task A_week_already_written_counts_as_already_written()
    {
        _digests.GetLatestByDateAsync(
                _memberId, WeekEnd, DigestAudience.Weekbook, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry { CardiMemberId = _memberId, LocalDate = WeekEnd, Audience = DigestAudience.Weekbook });
        using var capture = new JournalMetricCapture();

        await CreateSut().GenerateDueWeekbooksAsync(UtcNow);

        Assert.Contains(("weekbook", "already_written"), capture.Outcomes);
    }

    [Fact]
    public async Task A_week_too_thin_to_account_for_counts_as_no_readings()
    {
        SetupWeek(daysWithData: 3);
        using var capture = new JournalMetricCapture();

        await CreateSut().GenerateDueWeekbooksAsync(UtcNow);

        Assert.Contains(("weekbook", "no_readings"), capture.Outcomes);
    }

    [Fact]
    public async Task A_period_another_execution_holds_counts_as_claimed_elsewhere()
    {
        _unitOfWork.GenerationLeases
            .TryClaimAsync(
                Arg.Any<Guid>(), Arg.Any<GenerationWork>(), Arg.Any<DateOnly>(),
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)null);
        using var capture = new JournalMetricCapture();

        Assert.Equal(0, await CreateSut().GenerateDueWeekbooksAsync(UtcNow));

        Assert.Contains(("weekbook", "claimed_elsewhere"), capture.Outcomes);
        await _medicalAi.DidNotReceive().GenerateStructuredWithUsageAsync<DigestGenerationService.WeekbookAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The insert answering false — the partial unique index already held another execution's
    /// row, or the member is under an erasure hold — is not a book written. It used to be
    /// counted as one, which is the one figure on the run-finished line that could then not be
    /// trusted.
    /// </summary>
    [Fact]
    public async Task An_insert_that_stored_nothing_counts_as_write_refused_not_written()
    {
        _digests.AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>()).Returns(false);
        using var capture = new JournalMetricCapture();

        Assert.Equal(0, await CreateSut().GenerateDueWeekbooksAsync(UtcNow));

        Assert.Contains(("weekbook", "write_refused"), capture.Outcomes);
        Assert.DoesNotContain(("weekbook", "written"), capture.Outcomes);
    }

    [Fact]
    public async Task A_generation_that_throws_counts_as_failed_and_spares_the_pass()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns<IEnumerable<ActivityLog>>(_ => throw new InvalidOperationException("boom"));
        using var capture = new JournalMetricCapture();

        Assert.Equal(0, await CreateSut().GenerateDueWeekbooksAsync(UtcNow));

        Assert.Contains(("weekbook", "failed"), capture.Outcomes);
    }

    // ── The clock is resolved once per member per scope ──────────────────────

    /// <summary>
    /// Resolving the anchor timezone costs two reads, and the digest pass asks the same question
    /// of the same member once per book. The answer is memoised for the life of the service, which
    /// is one pass — so a second book's pass on the same instance reads no caregiver link again.
    /// </summary>
    [Fact]
    public async Task The_anchor_timezone_is_read_once_per_member_across_a_pass()
    {
        var sut = CreateSut();

        await sut.GenerateDueWeekbooksAsync(UtcNow);
        await sut.GenerateDueMonthbooksAsync(UtcNow);
        await sut.GenerateDueDaybooksAsync(UtcNow);

        await _links.Received(1).GetByCardiMemberIdAsync(_memberId);
    }
}

