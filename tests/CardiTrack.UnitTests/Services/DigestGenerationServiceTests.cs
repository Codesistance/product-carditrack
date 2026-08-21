using System.Globalization;
using System.Reflection;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Services.PromptContext;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

    /// <summary>
    /// Pins the summary due-ness rules: recomputed whenever the member's readings have moved
    /// since their last summary but no more often than the regeneration floor — unless the new
    /// readings are a problem, off the baseline, or a jump from yesterday, or an alert changed.
    /// Never while paused, never from silence — and one member's failure never costs another
    /// family theirs.
    /// </summary>
public class DigestGenerationServiceTests
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

    /// <summary>
    /// 09:30 UTC on a BST date — 10:30 in London, so the member's local day is the 10th.
    /// </summary>
    /// <remarks>
    /// Mid-morning rather than dawn, and deliberately so: the member is comfortably past
    /// <see cref="DigestDayProgress.DefaultWakeTime"/> and past
    /// <see cref="DigestDayProgress.EarlyDayHours"/>, which is the ordinary case the regeneration
    /// rules below are written about. The gates that hold before someone is up, and in the first
    /// hours after, get their own clocks — see the early-day tests.
    /// </remarks>
    private static readonly DateTime UtcNow = new(2026, 8, 10, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>The member's local day at <see cref="UtcNow"/>: the day a summary now describes.</summary>
    private static readonly DateOnly Today = new(2026, 8, 10);

    /// <summary>When the readings on hand last landed — half an hour before this pass.</summary>
    private static readonly DateTime DataLandedAt = UtcNow.AddMinutes(-30);

    public DigestGenerationServiceTests()
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

        // Defaults: one active London-anchored member whose data landed half an hour ago, and who
        // has never had a summary written.
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());
        SetupAnchorTimeZone("Europe/London");
        SetupActivity(DataLandedAt);
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns((DigestEntry?)null);
        // Still learning by default: no established baseline, so the prompt carries no usual
        // pattern — the shape every pre-existing expectation below was written against.
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);
        // A calm member by default: no alerts to change state, nothing already asked, and no
        // recent automated observations — so a proposed question is never treated as gap-backed
        // unless a test explicitly gives it something to back the gap with.
        _alerts.GetByCardiMemberAsync(_memberId, Arg.Any<bool>()).Returns([]);
        _realtimeAssessments.GetSinceAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _realtimeAssessments.GetLatestAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns((RealtimeAssessment?)null);
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>()).Returns([]);
        _questionnaires.HasPendingAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _questionnaires.GetLatestGeneratedAtAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns((DateTime?)null);
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A settled night",
                Summary = "A settled day: steady heart rate and a good night's sleep.",
            });
    }

    private CardiMember Member() => new()
    {
        Id = _memberId,
        Name = "Margaret Doe",
        DateOfBirth = new DateOnly(1948, 3, 2),
        Gender = Gender.Female,
        IsActive = true,
    };

    /// <param name="landedAt">
    /// Stamped explicitly rather than left to the entity base's wall-clock default: it is the
    /// value the recompute trigger compares against, so the tests must own it.
    /// </param>
    private void SetupActivity(DateTime landedAt, int? sleepMinutes = null)
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(call =>
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId,
                    Date = call.ArgAt<DateOnly>(2),
                    Steps = 5000,
                    RestingHeartRate = 68,
                    SleepMinutes = sleepMinutes,
                    CreatedDate = landedAt,
                },
            ]);
    }

    private void SetupTodayAndYesterday(DateTime todayLandedAt, int todayRestingHr, int yesterdayRestingHr)
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(call =>
            {
                var today = call.ArgAt<DateOnly>(2);
                return new[]
                {
                    new ActivityLog
                    {
                        CardiMemberId = _memberId,
                        Date = today.AddDays(-1),
                        RestingHeartRate = yesterdayRestingHr,
                        CreatedDate = todayLandedAt.AddDays(-1),
                    },
                    new ActivityLog
                    {
                        CardiMemberId = _memberId,
                        Date = today,
                        RestingHeartRate = todayRestingHr,
                        CreatedDate = todayLandedAt,
                    },
                };
            });
    }

    private void SetupAnchorTimeZone(string timeZoneId)
    {
        _links.GetByCardiMemberIdAsync(_memberId).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = _memberId, IsActive = true },
        ]);
        _users.GetByIdAsync(_userId).Returns(new User { Id = _userId, TimeZoneId = timeZoneId });
    }

    private DigestGenerationService CreateSut() =>
        new(_unitOfWork, _medicalAi, PromptContextFactory.Composer(_unitOfWork),
            PromptContextFactory.Encryption, InertStatusLineGenerator.Create(),
            NullLogger<DigestGenerationService>.Instance);

    /// <summary>
    /// The one integration pin on the batch hook: a stored digest regenerates the member's
    /// persisted status line, on the same pass. Uses a real generator over the shared substitutes
    /// (every other test in this suite uses the inert one so its call counts stay the digest's own).
    /// </summary>
    [Fact]
    public async Task SuccessfulDigest_RegeneratesTheStatusLine()
    {
        var statusLines = Substitute.For<IMemberStatusLineRepository>();
        statusLines.GetByCardiMemberAsync(_memberId).Returns((MemberStatusLine?)null);
        _unitOfWork.MemberStatusLines.Returns(statusLines);
        _medicalAi.GenerateStructuredAsync<StatusLineGenerationService.CurrentStatusAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StatusLineGenerationService.CurrentStatusAiResponse
            {
                Headline = "All steady",
                Message = "Steady today.",
            });
        var sut = new DigestGenerationService(
            _unitOfWork, _medicalAi, PromptContextFactory.Composer(_unitOfWork),
            PromptContextFactory.Encryption,
            new StatusLineGenerationService(
                _unitOfWork, _medicalAi, PromptContextFactory.Composer(_unitOfWork),
                NullLogger<StatusLineGenerationService>.Instance),
            NullLogger<DigestGenerationService>.Instance);

        var generated = await sut.GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await statusLines.Received(1).AddAsync(Arg.Is<MemberStatusLine>(s =>
            s.CardiMemberId == _memberId && s.Message == "Steady today."));
    }

    [Fact]
    public async Task Generates_ForAMemberWithNoSummaryYet()
    {
        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d =>
                d.CardiMemberId == _memberId &&
                d.Audience == DigestAudience.Family &&
                // Keyed by the day the text DESCRIBES — now the member's local day in progress,
                // because a summary recomputed on every update is only useful if it is current.
                d.LocalDate == Today &&
                d.Headline == "A settled night" &&
                d.Text.Contains("settled") &&
                d.GeneratedAtUtc == UtcNow),
            Arg.Any<CancellationToken>());
    }

    // The whole point of recomputation: readings that landed after the last summary was written
    // are readings that summary never saw.
    [Fact]
    public async Task Regenerates_WhenDataLandedAfterTheLastSummary()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today,
                // An hour before the data landed, so this exercises the data-moved trigger rather
                // than the regeneration floor, which would otherwise hold a summary this recent.
                GeneratedAtUtc = DataLandedAt.AddHours(-1),
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        // Appended, not overwritten: the earlier generation is the history behind this one.
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.LocalDate == Today && d.GeneratedAtUtc == UtcNow),
            Arg.Any<CancellationToken>());
    }

    // What keeps "recompute on every update" from meaning "re-run the fleet on every pass".
    [Fact]
    public async Task Skips_WhenNoDataHasLandedSinceTheLastSummary()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today,
                GeneratedAtUtc = DataLandedAt.AddMinutes(1),
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Unmoved daily rows never need the yardstick — fetching it here would be a baseline
        // query on every mostly-skipping pass.
        await _baselines.DidNotReceive().GetLatestByCardiMemberAsync(_memberId, Arg.Any<int>());
    }

    /// <summary>An edited log — a corrected or backfilled day — is new data too.</summary>
    [Fact]
    public async Task Regenerates_WhenAnExistingLogWasEditedAfterTheLastSummary()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId,
                    Date = Today,
                    Steps = 5200,
                    CreatedDate = UtcNow.AddHours(-6),
                    UpdatedDate = UtcNow.AddMinutes(-10),
                },
            ]);
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today,
                // Clear of the regeneration floor: the edit is what should trigger this, not age.
                GeneratedAtUtc = UtcNow.AddHours(-2),
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
    }

    /// <summary>
    /// The cost bound that lets the assessor re-run generation after every pass. A worn device
    /// uploads on nearly every pass, so "data has moved" alone would mean an inference every
    /// half hour — the floor is what decouples how often the jobs run from how many summaries a
    /// member accumulates, and ordinary new readings still sit behind it.
    /// </summary>
    [Fact]
    public async Task Skips_WhenTheLastSummaryIsTooRecent_EvenThoughNewDataHasLanded()
    {
        // Ten minutes old, and readings landed after it: the data gate would happily regenerate.
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today,
                GeneratedAtUtc = UtcNow.AddMinutes(-10),
            });
        SetupActivity(UtcNow.AddMinutes(-2));

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The floor is a minimum age, not a rounding: a summary exactly that old is due again. Pins
    /// the boundary so the interval can be retuned without the direction of the comparison
    /// quietly changing with it.
    /// </summary>
    [Fact]
    public async Task Regenerates_WhenTheLastSummaryIsExactlyTheFloorOld()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today,
                GeneratedAtUtc = UtcNow.AddHours(-1),
            });
        SetupActivity(UtcNow.AddMinutes(-2));

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
    }

    /// <summary>
    /// The floor bounds regeneration, never the first summary: a member who has been quiet and
    /// starts uploading again is caught by the very next pass, which is the freshness the
    /// half-hourly cadence was bought for.
    /// </summary>
    [Fact]
    public async Task Generates_ImmediatelyForAMemberWithNoSummaryYet_WhateverTheFloor()
    {
        SetupActivity(UtcNow.AddMinutes(-1));

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
    }

    // ---- What the member's own day allows ----
    //
    // The floor above assumes that data moving means the wording should move. Early in a member's
    // day that inverts: the readings move because the day is filling up from nothing, so every pass
    // found new data and bought a generation to say the same thing about the same near-empty running
    // total. One member's morning produced a summary roughly every twenty minutes from local
    // midnight, each re-deriving that a just-woken person had not walked far.

    /// <summary>05:00 in London — the member is not up, so there is no today to describe yet.</summary>
    private static readonly DateTime BeforeWake = new(2026, 8, 10, 4, 0, 0, DateTimeKind.Utc);

    /// <summary>08:00 in London — up an hour, inside the early-day window.</summary>
    private static readonly DateTime JustAfterWake = new(2026, 8, 10, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Skips_BeforeTheMemberIsUp_LeavingYesterdaysSummaryStanding()
    {
        GivenPreviousSummary(BeforeWake.AddHours(-8));
        SetupActivity(BeforeWake.AddMinutes(-2));

        var generated = await CreateSut().GenerateDueDigestsAsync(BeforeWake);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The gate above bounds regeneration, never the first summary — a member with nothing on file
    /// gets one whatever the hour, the same stance the 20-minute floor takes.
    /// </summary>
    [Fact]
    public async Task Generates_BeforeWake_ForAMemberWithNoSummaryYet()
    {
        SetupActivity(BeforeWake.AddMinutes(-2));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(BeforeWake));
    }

    /// <summary>
    /// A new local day is new information by itself, so the first summary of one is not held back —
    /// the widened floor only applies once the day in progress already has a summary. Without this
    /// a caregiver's morning card would still be describing yesterday.
    /// </summary>
    [Fact]
    public async Task Generates_TheFirstSummaryOfANewDay_EvenEarly()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today.AddDays(-1),
                GeneratedAtUtc = JustAfterWake.AddHours(-10),
            });
        SetupActivity(JustAfterWake.AddMinutes(-2));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(JustAfterWake));
    }

    /// <summary>
    /// The one that costs the dozen inferences: today already has a summary, the member has been up
    /// an hour, and new readings keep landing because the day is filling up.
    /// </summary>
    [Fact]
    public async Task Skips_EarlyInTheDay_WhenTodayAlreadyHasASummary()
    {
        GivenPreviousSummary(JustAfterWake.AddMinutes(-25));
        SetupActivity(JustAfterWake.AddMinutes(-2));

        var generated = await CreateSut().GenerateDueDigestsAsync(JustAfterWake);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A floor, not a freeze: the widened one is past, so the ordinary cycle resumes.</summary>
    [Fact]
    public async Task Regenerates_EarlyInTheDay_OnceTheWidenedFloorHasPassed()
    {
        GivenPreviousSummary(JustAfterWake.AddHours(-2));
        SetupActivity(JustAfterWake.AddMinutes(-2));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(JustAfterWake));
    }

    /// <summary>
    /// A bad morning is still a morning a caregiver hears about at once. The same waivers that cut
    /// through the hourly floor cut through the widened one — this is what keeps the cost gate
    /// from becoming a safety gate.
    /// </summary>
    [Fact]
    public async Task Regenerates_EarlyInTheDay_WhenAnAlertIsRaised()
    {
        GivenPreviousSummary(JustAfterWake.AddMinutes(-5));
        GivenAlerts(AnAlert(triggeredAt: JustAfterWake.AddMinutes(-2), resolved: false));
        SetupActivity(JustAfterWake.AddMinutes(-2));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(JustAfterWake));
    }

    // ---- The other end of the night ----
    //
    // IsBeforeWake above declines the small hours outright. Between a member's bedtime and
    // midnight there is a real day, and it has just finished — so the floor stops lifting there
    // rather than the generator refusing, and every waiver still cuts through. Measured over a
    // full day in dev, ordinary regeneration running around the clock was 269 of 289 MedGemma
    // calls; this closes the stretch no threshold covered.

    /// <summary>22:30 in London — past the default 22:00 bedtime, before midnight.</summary>
    private static readonly DateTime AfterBedtime = new(2026, 8, 10, 21, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// The floor does not lift after bedtime however old the last summary is. Without this the
    /// ordinary cycle ran at full rate until midnight, rewriting a finished day for a household
    /// that had gone to bed.
    /// </summary>
    [Fact]
    public async Task Skips_AfterBedtime_EvenWhenTheFloorHasLongPassed()
    {
        GivenPreviousSummary(AfterBedtime.AddHours(-3));
        SetupActivity(AfterBedtime.AddMinutes(-2));

        var generated = await CreateSut().GenerateDueDigestsAsync(AfterBedtime);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An evening that goes wrong reaches a caregiver at 22:30, not at breakfast. This is the
    /// difference between the two ends of the night: before waking there is no day to describe, so
    /// the generator declines outright, whereas here the day is real and only its wording is being
    /// held back — which a jump from yesterday overrides like any other floor.
    /// </summary>
    [Fact]
    public async Task Regenerates_AfterBedtime_WhenReadingsJumpedFromYesterday()
    {
        GivenPreviousSummary(AfterBedtime.AddMinutes(-5));
        SetupTodayAndYesterday(
            todayLandedAt: AfterBedtime.AddMinutes(-2),
            todayRestingHr: 80,
            yesterdayRestingHr: 60);

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(AfterBedtime));
    }

    /// <summary>And an alert raised after bedtime, for the same reason.</summary>
    [Fact]
    public async Task Regenerates_AfterBedtime_WhenAnAlertIsRaised()
    {
        GivenPreviousSummary(AfterBedtime.AddMinutes(-5));
        GivenAlerts(AnAlert(triggeredAt: AfterBedtime.AddMinutes(-2), resolved: false));
        SetupActivity(AfterBedtime.AddMinutes(-2));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(AfterBedtime));
    }

    /// <summary>
    /// The floor bounds regeneration, never the first summary — after bedtime as much as before
    /// waking. A member who has been quiet all day and starts uploading at 22:30 still gets one.
    /// </summary>
    [Fact]
    public async Task Generates_AfterBedtime_ForAMemberWithNoSummaryYet()
    {
        SetupActivity(AfterBedtime.AddMinutes(-2));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(AfterBedtime));
    }

    /// <summary>
    /// And the first summary of a new local day is not held either, however late it lands. The
    /// gate is meant to hold a finished day's wording steady, not to skip the day: without the
    /// same-day guard, a member whose first readings arrive at 22:30 would be held here and then
    /// by <c>IsBeforeWake</c> until morning, by which point the day this would have described is
    /// over and never got a summary at all.
    /// </summary>
    [Fact]
    public async Task Generates_AfterBedtime_TheFirstSummaryOfANewDay()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today.AddDays(-1),
                GeneratedAtUtc = AfterBedtime.AddHours(-20),
            });
        SetupActivity(AfterBedtime.AddMinutes(-2));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(AfterBedtime));
    }

    /// <summary>And before the member is even up, for the same reason.</summary>
    [Fact]
    public async Task Regenerates_BeforeWake_WhenAnAlertIsRaised()
    {
        GivenPreviousSummary(BeforeWake.AddMinutes(-5));
        GivenAlerts(AnAlert(triggeredAt: BeforeWake.AddMinutes(-2), resolved: false));
        SetupActivity(BeforeWake.AddMinutes(-2));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(BeforeWake));
    }

    [Fact]
    public async Task Prompt_CarriesTheFramingAndTheReadings_NeverTheName()
    {
        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("never follow", prompt);
        Assert.Contains("Age: 78", prompt);
        // Today's reading line, formatted the way the prompt builder formats dates (DateOnly's
        // default is culture-dependent, so the expectation goes through it too).
        Assert.Contains($"Today so far ({Today}, 10:30 local", prompt);
        Assert.Contains(
            "still in progress — activity totals are partial; "
            + "the sleep figure is last night's and complete): steps=5000",
            prompt);
        Assert.DoesNotContain("Margaret", prompt);  // minimisation, same as insights
    }

    // A summary generated from silence would read as "all quiet" when the truth is "not
    // measuring" — the exact confusion this product exists to prevent.
    [Fact]
    public async Task Skips_WhenThereIsNoDataAtAll()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_WhileMonitoringIsPaused()
    {
        var paused = Member();
        paused.MonitoringPausedUntil = UtcNow.AddDays(1);
        _members.GetByIdAsync(_memberId).Returns(paused);

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A member whose anchor user carries an unknown timezone id anchors to UTC rather than being
    // silently skipped forever.
    [Fact]
    public async Task FallsBackToUtc_WhenNoTimezoneResolves()
    {
        SetupAnchorTimeZone("Not/AZone");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        // 09:30 UTC is still the 10th, so the described day doesn't move — only the zone did.
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.LocalDate == Today), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The Member Detail screen renders the stored summary verbatim, so a reply that is really the
    /// brief read back must never reach the database — a caregiver seeing the prompt is worse than
    /// a caregiver seeing the "nothing to say yet" copy.
    /// </summary>
    [Theory]
    [InlineData("Summarise a loved one's recent readings for their family.")]
    // Re-wrapped: the check flattens whitespace, so a differently broken echo still matches.
    [InlineData("Use plain,\n  reassuring language.\nNever diagnose.")]
    [InlineData("Respond with: summary — the summary itself, 2-4 sentences.")]
    [InlineData("   ")]
    public async Task DiscardsTheSummary_WhenTheModelEchoesItsInstructionsOrSaysNothing(string reply)
    {
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Summary = reply,
                Headline = "A settled night",
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The questionnaire loop exists so the next summary can read the day against what the family
    /// said. A summary that is those answers read back is the loop failing, and the Member Detail
    /// screen would show the caregiver their own words as if the service had noticed something.
    /// </summary>
    [Fact]
    public async Task DiscardsTheSummary_WhenItIsTheFamilyAnswersReadBack()
    {
        GivenAnsweredQuestion(
            "Has anything changed at home recently?", "She moved bedrooms last week.");
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A bedroom move",
                Summary = "She moved bedrooms last week.",
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Using an answer to interpret a reading is the point. Verbatim overlap is allowed when the
    /// leftover wording is still a reading of the day, not a recap with a clause glued on.
    /// </summary>
    [Fact]
    public async Task StoresTheSummary_WhenAFamilyAnswerIsUsedToReadTheDay()
    {
        GivenAnsweredQuestion(
            "Has anything changed at home recently?", "She moved bedrooms last week.");
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A shorter night",
                Summary = "She moved bedrooms last week, so last night was unsettled "
                    + "and her resting rate sat above usual.",
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Text.Contains("resting rate", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A quiz transcript in the prompt is what MedGemma recites. The family already knows the
    /// exchange; the model needs the fact.
    /// </summary>
    [Fact]
    public async Task PromptCarriesFamilyAnswersAsFacts_NotAsAQuizTranscript()
    {
        GivenAnsweredQuestion(
            "Has anything changed at home recently?", "She moved bedrooms last week.");

        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.NotNull(prompt);
        Assert.Contains("She moved bedrooms last week.", prompt);
        Assert.Contains("never retell them", prompt);
        Assert.DoesNotContain("Q:", prompt);
        Assert.DoesNotContain("Has anything changed at home recently?", prompt);
    }

    [Fact]
    public async Task StoresTheModelsSummary_Trimmed()
    {
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "  \"A quiet stretch.\"  ",
                Summary = "  A quiet, steady day.\n",
            });

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        // The headline is a card title: quotes and a trailing stop are stripped rather than shown.
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Text == "A quiet, steady day." && d.Headline == "A quiet stretch"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A headline that came back as prose, or as the brief read back, is dropped — the apps title
    /// the card themselves — but the summary under it is still worth storing.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Respond with: headline, a label of two to five words naming what this is about.")]
    [InlineData("Everything about the last day looked broadly settled, with steady readings "
                + "through the evening and a full night's sleep afterwards, which is what we hoped for.")]
    public async Task StoresTheSummaryWithoutAHeadline_WhenTheHeadlineIsUnusable(string headline)
    {
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = headline,
                Summary = "A quiet, steady day.",
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Headline == null && d.Text == "A quiet, steady day."),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Every phrase the echo guard watches for has to appear in the prompt, wholly inside one of
    /// its lines — that is the whole basis of the check, and the guard now spans two files since
    /// the prompt opens with the shared tone block. A phrase that drifted out of the prompt, or a
    /// prompt line that re-wrapped around one, would leave the guard passing while catching
    /// nothing: it would still run, still find no match, and still let the model's own brief
    /// through to a caregiver.
    /// </summary>
    [Fact]
    public async Task EveryPhraseTheEchoGuardWatchesFor_IsOnOneLineOfThePrompt()
    {
        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);
        Assert.NotNull(prompt);

        var echoes = (string[])typeof(DigestGenerationService)
            .GetField("InstructionEchoes", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        var lines = prompt.Split('\n').Select(l => l.Trim()).ToList();

        Assert.NotEmpty(echoes);
        Assert.All(echoes, echo => Assert.Contains(
            lines,
            line => line.Contains(echo, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- Which day a reading belongs to ----

    /// <summary>
    /// The failure this format exists to prevent: two rows of identical shape, told apart only by
    /// a date the model has to relate to a "today" nobody named, produced a family summary that
    /// credited yesterday's step total to today while taking the same sentence's sleep figure from
    /// the correct row. Each line now opens with which day it is.
    /// </summary>
    [Fact]
    public async Task EveryReadingLineSaysWhichDayItIs_BeforeTheNumbers()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today.AddDays(-1), Steps = 3835,
                    RestingHeartRate = 62, CreatedDate = DataLandedAt,
                },
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today, Steps = 3442,
                    RestingHeartRate = 58, CreatedDate = DataLandedAt,
                },
            ]);

        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);
        Assert.NotNull(prompt);

        Assert.Contains($"Yesterday ({Today.AddDays(-1)}, complete day): steps=3835", prompt);
        // Today's label also carries the clock — see DigestDayProgress and the tests below for why
        // "partial" on its own was not enough. Asserted in two halves so those words are pinned
        // without this test also owning the wording of the clock phrase.
        Assert.Contains($"Today so far ({Today}, 10:30 local", prompt);
        Assert.Contains(
            "still in progress — activity totals are partial; "
            + "the sleep figure is last night's and complete): steps=3442",
            prompt);

        // The label leads the line. A note trailing the numbers arrives after the model has read
        // them, which is how the wrong day's total got attributed in the first place.
        foreach (var line in prompt.Split('\n').Where(l => l.Contains("steps=")))
            Assert.Matches(@"^\s*(Today so far|Yesterday|\d+ days ago) \(", line);
    }

    [Fact]
    public async Task TheReadingsAreOrderedOldestFirst_AndTheHeaderSaysSo()
    {
        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        // The header used to read "Today so far, and yesterday" over rows running the other way.
        Assert.Contains("oldest first", prompt);
    }

    // ---- The usual pattern: the yardstick a reading is read against ----

    private static PatternBaseline EstablishedBaseline() => new()
    {
        PeriodDays = 30,
        AvgSteps = 6000,
        AvgRestingHeartRate = 62,
        StdDevHeartRate = 10.0m,
        AvgSleepMinutes = 420,
    };

    private async Task<string> CapturePromptAsync()
    {
        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);
        Assert.NotNull(prompt);
        return prompt;
    }

    /// <summary>
    /// The failure this section exists to prevent: a summary called a member's short night "a
    /// good night's sleep" because nothing in the prompt said what a normal night was for them.
    /// The model is not asked to know the member's normal — it is handed it.
    /// </summary>
    [Fact]
    public async Task Prompt_CarriesTheUsualPattern_OnceABaselineIsEstablished()
    {
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(EstablishedBaseline());

        var prompt = await CapturePromptAsync();

        Assert.Contains("--- Usual pattern (30-day average) ---", prompt);
        Assert.Contains("about 6,000 steps a day", prompt);
        Assert.Contains("a resting heart rate around 62 bpm", prompt);
        Assert.Contains("about 7.0 hours of sleep a night", prompt);
        Assert.Contains("read each reading against it", prompt);
        Assert.DoesNotContain("Computed observations", prompt);
    }

    [Fact]
    public async Task Prompt_CarriesNoUsualPattern_WhileTheMemberIsStillBeingLearned()
    {
        var prompt = await CapturePromptAsync();

        Assert.DoesNotContain("Usual pattern", prompt);
    }

    /// <summary>
    /// Deterministic code computes the verdict on last night, the model only phrases it — the
    /// same division of labour as the rest of the pipeline, and the same threshold as the
    /// irregular-sleep alert rule, so the summary can never soothe over a night the alert engine
    /// pages about. Last night is today's row: sleep is attributed to the day it ended on.
    /// </summary>
    [Fact]
    public async Task Prompt_SaysPlainly_WhenLastNightWasWellShortOfTheUsual()
    {
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(EstablishedBaseline());
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today.AddDays(-1), Steps = 5800,
                    SleepMinutes = 430, CreatedDate = DataLandedAt,
                },
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today, Steps = 900,
                    SleepMinutes = 216, CreatedDate = DataLandedAt,
                },
            ]);

        var prompt = await CapturePromptAsync();

        // In the computed observations now, not the usual-pattern block — that is the section the
        // prompt tells the model to lead with, and a finding outside it loses to one inside it.
        Assert.Contains(
            "- Last night: 3.6 hours of sleep (usual 7.0) — well short of their usual.",
            prompt);
    }

    /// <summary>
    /// The prompt is model input and a cacheable fixed-prefix construction, so no number in it
    /// may vary with the host's ambient culture — no locale is pinned in any of the service
    /// Dockerfiles, and under a European one the grouped step figure "6,000" renders as "6.000",
    /// which a model can read as six.
    /// </summary>
    [Fact]
    public async Task Prompt_FormatsEveryFigureInvariantly_WhateverTheHostCulture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(EstablishedBaseline());
            _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
                .Returns(
                [
                    new ActivityLog
                    {
                        CardiMemberId = _memberId, Date = Today, Steps = 900,
                        SleepMinutes = 216, CreatedDate = DataLandedAt,
                    },
                ]);

            var prompt = await CapturePromptAsync();

            Assert.Contains("about 6,000 steps a day", prompt);
            Assert.Contains("about 7.0 hours of sleep a night", prompt);
            Assert.Contains("- Last night: 3.6 hours of sleep (usual 7.0)", prompt);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // A night inside the ordinary band earns no note: the note is for the reading that must not
    // be soothed over, not a running commentary.
    [Fact]
    public async Task Prompt_CarriesNoSleepNote_ForAnOrdinaryNight()
    {
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(EstablishedBaseline());
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today, Steps = 900,
                    SleepMinutes = 400, CreatedDate = DataLandedAt,
                },
            ]);

        var prompt = await CapturePromptAsync();

        Assert.Contains("--- Usual pattern (30-day average) ---", prompt);
        Assert.DoesNotContain("- Last night:", prompt);
    }

    // The note reads the night off today's row only: yesterday's row is the night before last,
    // and a note about it would flag old news as tonight's.
    [Fact]
    public async Task TheSleepNote_NeverReadsYesterdaysNight_AsLastNights()
    {
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(EstablishedBaseline());
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today.AddDays(-1), Steps = 5800,
                    SleepMinutes = 216, CreatedDate = DataLandedAt,
                },
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today, Steps = 900,
                    CreatedDate = DataLandedAt,
                },
            ]);

        var prompt = await CapturePromptAsync();

        Assert.DoesNotContain("- Last night:", prompt);
    }

    /// <summary>
    /// The failure this section exists to prevent: a raised resting rate on a day of almost no
    /// steps was recited as "heart rate is this, steps are that" instead of named as a still-day
    /// finding. The pairing is computed here, same thresholds as the alert rules.
    /// </summary>
    [Fact]
    public async Task Prompt_NamesQuietAndRaised_WhenYesterdayWasStillWithARaisedRate()
    {
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(new PatternBaseline
        {
            PeriodDays = 30,
            AvgSteps = 6000,
            AvgRestingHeartRate = 71,
            StdDevHeartRate = 2.0m,
            AvgSleepMinutes = 420,
        });
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today.AddDays(-1), Steps = 1200,
                    RestingHeartRate = 88, CreatedDate = DataLandedAt,
                },
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today, Steps = 4350,
                    RestingHeartRate = 71, CreatedDate = DataLandedAt,
                },
            ]);

        var prompt = await CapturePromptAsync();

        Assert.Contains("--- Computed observations ---", prompt);
        Assert.Contains(
            "Yesterday: resting heart rate 88 bpm (usual 71) with 1,200 steps (usual 6,000) "
            + "— these findings on a still day, not a day of walking.",
            prompt);
        Assert.DoesNotContain("Today so far: resting heart rate", prompt);
    }

    /// <summary>
    /// A morning's few thousand against a 6,000-step usual is not stillness. The digest job
    /// runs at 06:30 local in this suite, well before the afternoon floor.
    /// </summary>
    [Fact]
    public async Task Prompt_DoesNotCallAMorningShortfallAStillDay()
    {
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(EstablishedBaseline());
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today, Steps = 900,
                    RestingHeartRate = 62, CreatedDate = DataLandedAt,
                },
            ]);

        var prompt = await CapturePromptAsync();

        Assert.DoesNotContain("Computed observations", prompt);
        Assert.DoesNotContain("not a day of walking", prompt);
    }

    /// <summary>
    /// The same two readings the Daybook renders as <c>bloodOxygen=97.5%</c> and
    /// <c>breathingRate=14.2/min</c> reached this prompt as a bare 97.5 and a bare 14.2, leaving
    /// the model to infer that one is a percentage and the other a rate per minute. Sleep stages
    /// put an "=" inside an "=", the one place on the line where the key-and-value shape every
    /// other figure follows broke.
    /// </summary>
    [Fact]
    public async Task Prompt_GivesTheUnitsForOxygenAndBreathing_AndOneEqualsPerFigure()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today, Steps = 4350,
                    SpO2Average = 96.4m, BreathingRate = 14.2m,
                    DeepSleepMinutes = 60, LightSleepMinutes = 200, RemSleepMinutes = 80,
                    CreatedDate = DataLandedAt,
                },
            ]);

        var prompt = await CapturePromptAsync();

        Assert.Contains("SpO2=96.4%", prompt);
        Assert.Contains("breathing=14.2/min", prompt);
        Assert.Contains("sleepStages(min)=deep 60/light 200/rem 80", prompt);
        Assert.DoesNotContain("deep=60", prompt);
    }

    /// <summary>
    /// The brief's conditional has to name the heading the context source renders, or it is a
    /// conditional on a section that never appears under that name — the mistake the Daybook's own
    /// conditions heading was made a const to prevent. And the absent case has to be stated: the
    /// Daybook says never to mention weather when the section is missing, and this brief did not,
    /// which left the one prompt that runs many times a day free to invent it.
    /// </summary>
    [Fact]
    public async Task Prompt_NamesTheConditionsHeadingItRenders_AndForbidsWeatherWhenItIsAbsent()
    {
        var prompt = await CapturePromptAsync();

        var rule = prompt.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Single(l => l.StartsWith(
                $"If \"{EnvironmentalContextSource.RecentConditionsLabel}\" is present,",
                StringComparison.Ordinal));

        Assert.EndsWith("when it is absent, never mention weather at all.", rule);
    }

    [Fact]
    public async Task Prompt_CarriesActiveMinutesAndMaxHeartRate_WhenTheDeviceReportedThem()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today, Steps = 4350,
                    ActiveMinutes = 42, RestingHeartRate = 71, AvgHeartRate = 84,
                    MaxHeartRate = 108, SpO2Average = 96.4m, CreatedDate = DataLandedAt,
                },
            ]);

        var prompt = await CapturePromptAsync();

        Assert.Contains("steps=4350", prompt);
        Assert.Contains("activeMinutes=42", prompt);
        Assert.Contains("HR=71", prompt);
        Assert.Contains("HR_avg=84", prompt);
        Assert.Contains("HR_max=108", prompt);
        Assert.Contains("SpO2=96.4", prompt);
        Assert.DoesNotContain("sleep(night ending that morning)=min", prompt);
        Assert.DoesNotContain("steps=,", prompt);
        Assert.DoesNotContain("HR=,", prompt);
    }

    [Fact]
    public async Task Prompt_CarriesUsualActiveMinutes_WhenTheBaselineHasThem()
    {
        var baseline = EstablishedBaseline();
        baseline.AvgActiveMinutes = 48;
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(baseline);

        var prompt = await CapturePromptAsync();

        Assert.Contains("about 48 active minutes a day", prompt);
    }

    // ---- A summary cannot credit today with steps the member has not walked ----

    private void ReturnsSummary(string summary) =>
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A settled night",
                Summary = summary,
            });

    /// <summary>
    /// Steps within a day only rise, so a figure above the running total is one the member has not
    /// walked yet — the rare claim a generated sentence can be checked against rather than trusted
    /// on. 5000 is today's total in the default setup.
    /// </summary>
    [Fact]
    public async Task DiscardsTheSummary_WhenItCreditsTodayWithStepsNotYetWalked()
    {
        ReturnsSummary("They walked quite a bit today, around 5800 steps. Their heart rate was steady.");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The summary that prompted all of this, with the readings behind it: yesterday 3,835 steps,
    /// today 3,442 so far, and a sentence crediting today with "around 3800" — yesterday's total,
    /// rounded, on the wrong day. The sleep figure in the same breath came off the right row,
    /// which is what marked it as a row mix-up rather than an invention.
    /// </summary>
    [Fact]
    public async Task DiscardsTheSummary_ThatAttributedYesterdaysStepsToToday()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(
            [
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today.AddDays(-1), Steps = 3835,
                    SleepMinutes = 412, CreatedDate = DataLandedAt,
                },
                new ActivityLog
                {
                    CardiMemberId = _memberId, Date = Today, Steps = 3442,
                    SleepMinutes = 230, CreatedDate = DataLandedAt,
                },
            ]);
        ReturnsSummary(
            "Your loved one had a good night's sleep last night, getting over 230 minutes in bed. "
            + "They also walked quite a bit today, around 3800 steps. Their heart rate was steady "
            + "and low during the day. Overall, things seem settled.");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllowsAnHonestRoundingOfTodaysSteps()
    {
        // A model told to prefer a phrase to a figure and then asked for one will round. "Around
        // 5,000" of 5,000 is a fair description; the guard is for a different day's number.
        ReturnsSummary("They walked around 5000 steps today, much as usual.");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
    }

    /// <summary>
    /// The failure was a misattribution, not an invention: the figure was real, it belonged to
    /// another day. A guard that ignored which day a sentence named would reject an honest mention
    /// of yesterday and still let the misattribution through.
    /// </summary>
    [Fact]
    public async Task LeavesAlone_AFigureAttributedToADayItCouldBelongTo()
    {
        ReturnsSummary("Yesterday they managed 8000 steps. Today has been quieter so far.");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
    }

    [Fact]
    public async Task LeavesAlone_ASummaryThatQuotesNoStepFigureAtAll()
    {
        // The point of the tone block: a phrase where a figure would do. Nothing to check.
        ReturnsSummary("They have been up and about today, much as they usually are.");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
    }

    // ---- Suggestion: one usable message or none at all ----

    private void ReturnsSuggestion(string? suggestion) =>
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A settled night",
                Summary = "A quiet, steady day.",
                Suggestion = suggestion,
            });

    [Fact]
    public async Task StoresTheSuggestion_Trimmed()
    {
        // A model that formatted its own list: the leading bullet is the model's, not the
        // suggestion's, and it would render as a literal character in the app.
        ReturnsSuggestion("  - Ask about the early waking when you call  ");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == "Ask about the early waking when you call"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoresNoSuggestion_WhenTheModelReturnedNone()
    {
        // The column is nullable precisely so this is representable; an empty string would make
        // the apps decide what an empty section looks like.
        ReturnsSuggestion(null);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoresNoSuggestion_WhenItRestatesTheInstructions()
    {
        ReturnsSuggestion("Respond with: one way to support them");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == null && d.Text == "A quiet, steady day."),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The failure this prompt change is about: the instructions and the reply schema used to
    /// carry example suggestions, and those examples came back word for word for member after
    /// member. The examples are gone; this is the backstop that keeps a return to them off screen.
    /// </summary>
    [Fact]
    public async Task DropsTheSuggestion_WhenItIsThePromptsOldExample()
    {
        ReturnsSuggestion("Ask how they slept");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == null), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The generic phrase is dropped whole, not as a substring — the same words carried on into
    /// something a family could actually act on are what the prompt now asks for.
    /// </summary>
    [Fact]
    public async Task KeepsASuggestionThatCarriesAGenericOpeningIntoSomethingSpecific()
    {
        ReturnsSuggestion("Ask how they slept when you call tonight");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == "Ask how they slept when you call tonight"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A bare category of caring is not a suggestion; the prompt says so and this holds it.</summary>
    [Fact]
    public async Task DropsTheSuggestion_WhenItIsABareCategoryOfCaring()
    {
        ReturnsSuggestion("Check in");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == null), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The relaxed guardrail allows a reminder tied to a known routine fact, like a scheduled
    /// medication — but naming or guessing at what might be wrong is still a diagnosis, and the
    /// prompt asking nicely is not trusted alone to keep it out.
    /// </summary>
    [Theory]
    [InlineData("This could be a sign of afib, worth watching")]
    [InlineData("This looks like it could be a heart condition")]
    public async Task DropsTheSuggestion_WhenItNamesOrGuessesAtACondition(string suggestion)
    {
        ReturnsSuggestion(suggestion);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == null), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: the backstop originally matched the bare word "condition", which would have
    /// dropped these — none of them name or guess at anything medical.
    /// </summary>
    [Theory]
    [InlineData("Ask if they'd like a walk given today's warm conditions")]
    [InlineData("Check their walking shoes are still in good condition")]
    [InlineData("Make sure the air conditioning is on before they nap")]
    public async Task KeepsASuggestion_ThatMentionsConditionWithoutAMedicalMeaning(string suggestion)
    {
        ReturnsSuggestion(suggestion);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == suggestion), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeepsAMedicationReminderTiedToAKnownRoutine()
    {
        ReturnsSuggestion("Remind Dad to take his evening medication if he hasn't yet");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == "Remind Dad to take his evening medication if he hasn't yet"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AsksForASuggestionThatNeverNamesACondition()
    {
        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.NotNull(prompt);
        Assert.Contains("never name or guess at a medical condition", prompt);
        Assert.Contains("never worded as something the", prompt);
    }

    /// <summary>
    /// The prompt asks for suggestions the readings earned, and carries no example that could be
    /// returned as one — an example beside the field is what the model reached for before.
    /// </summary>
    [Fact]
    public async Task AsksForSuggestionsTheReadingsEarned_AndOffersNoExampleToCopy()
    {
        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.NotNull(prompt);
        Assert.Contains("must answer something in the readings or computed observations above", prompt);
        Assert.Contains("equally true for any person on any day", prompt);
        Assert.DoesNotContain("suggest checking in", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("change of routine", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new room", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("difficult week", prompt, StringComparison.OrdinalIgnoreCase);
        foreach (var parroted in new[]
                 {
                     "Ask how they slept", "Suggest a short walk together", "Make their favourite tea",
                 })
        {
            Assert.DoesNotContain(parroted, prompt);
        }
    }

    [Fact]
    public async Task OneMembersFailure_DoesNotCostTheOthersTheirSummary()
    {
        var failingId = Guid.NewGuid();
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([failingId, _memberId]);
        _members.GetByIdAsync(failingId).ThrowsAsync(new InvalidOperationException("boom"));

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.CardiMemberId == _memberId), Arg.Any<CancellationToken>());
    }

    // ---- Alert state waives the regeneration gates ----

    /// <summary>
    /// The floor exists so a summary whose wording barely moves does not cost an inference. An
    /// alert raised since the last one is the case where the wording does move, and making a
    /// caregiver wait twenty minutes to read it would be the floor working against its own purpose.
    /// </summary>
    [Fact]
    public async Task RegeneratesInsideTheFloor_WhenAnAlertWasRaisedSinceTheLastSummary()
    {
        GivenPreviousSummary(UtcNow.AddMinutes(-5));
        GivenAlerts(AnAlert(triggeredAt: UtcNow.AddMinutes(-2), resolved: false));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(UtcNow));
    }

    /// <summary>
    /// Resolution counts as much as the alert did. A summary still hedging about an episode that
    /// has ended reads as a service that has not noticed — the same failure, other direction.
    /// </summary>
    [Fact]
    public async Task RegeneratesInsideTheFloor_WhenAnAlertWasResolvedSinceTheLastSummary()
    {
        GivenPreviousSummary(UtcNow.AddMinutes(-5));
        GivenAlerts(AnAlert(UtcNow.AddHours(-3), resolved: true, updatedAt: UtcNow.AddMinutes(-1)));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(UtcNow));
    }

    /// <summary>
    /// A resolution arriving when no new readings have landed must still be written: the data has
    /// not moved, but what the summary should say has.
    /// </summary>
    [Fact]
    public async Task RegeneratesOnAlertChange_EvenWhenTheReadingsHaveNotMoved()
    {
        GivenPreviousSummary(UtcNow.AddHours(-2));
        SetupActivity(UtcNow.AddHours(-3));
        GivenAlerts(AnAlert(UtcNow.AddHours(-4), resolved: true, updatedAt: UtcNow.AddMinutes(-10)));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(UtcNow));
    }

    [Fact]
    public async Task StillSkipsInsideTheFloor_WhenTheAlertPredatesTheLastSummary()
    {
        GivenPreviousSummary(UtcNow.AddMinutes(-5));
        GivenAlerts(AnAlert(UtcNow.AddHours(-6), resolved: false));

        Assert.Equal(0, await CreateSut().GenerateDueDigestsAsync(UtcNow));
    }

    /// <summary>
    /// A yellow window is the assessor saying something is off, without (yet) paging. That used
    /// to ride the ordinary cycle, which is how a dire hour sat behind a twenty-minute-old
    /// "settled day" card. It waives the floor the same way an alert does.
    /// </summary>
    [Fact]
    public async Task RegeneratesInsideTheFloor_WhenAYellowAssessmentLandedSinceTheLastSummary()
    {
        GivenPreviousSummary(UtcNow.AddMinutes(-5));
        GivenLatestAssessment(AlertSeverity.Yellow, score: 1.2, generatedAt: UtcNow.AddMinutes(-1));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(UtcNow));
    }

    /// <summary>
    /// The SSA score is the jump the assessor already computed. A green window with a jump is
    /// still a change in what the summary should say — the model calling it "low" does not make
    /// the reading ordinary.
    /// </summary>
    [Fact]
    public async Task RegeneratesInsideTheFloor_WhenTheSsaScoreIsAJump()
    {
        GivenPreviousSummary(UtcNow.AddMinutes(-5));
        GivenLatestAssessment(AlertSeverity.Green, DigestRefreshRules.SampleJumpScore, UtcNow.AddMinutes(-1));

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(UtcNow));
    }

    [Fact]
    public async Task StillSkipsInsideTheFloor_WhenTheAssessmentPredatesTheLastSummary()
    {
        GivenPreviousSummary(UtcNow.AddMinutes(-5));
        GivenLatestAssessment(AlertSeverity.Red, score: 8.0, generatedAt: UtcNow.AddMinutes(-6));

        Assert.Equal(0, await CreateSut().GenerateDueDigestsAsync(UtcNow));
    }

    /// <summary>
    /// Last night well short of the usual is the same off-baseline the statistical engine would
    /// page about. New data inside the floor must still rewrite the summary.
    /// </summary>
    [Fact]
    public async Task RegeneratesInsideTheFloor_WhenReadingsDivergeFromBaseline()
    {
        GivenPreviousSummary(UtcNow.AddMinutes(-5));
        SetupActivity(UtcNow.AddMinutes(-2), sleepMinutes: 250);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 30,
            AvgSleepMinutes = 420,
        });

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(UtcNow));
    }

    /// <summary>
    /// A 30% jump in resting heart rate from yesterday, even while still learning (no baseline).
    /// The floor is for wording that barely moves, not for a jump like this.
    /// </summary>
    [Fact]
    public async Task RegeneratesInsideTheFloor_WhenReadingsJumpedFromYesterday()
    {
        GivenPreviousSummary(UtcNow.AddMinutes(-5));
        SetupTodayAndYesterday(
            todayLandedAt: UtcNow.AddMinutes(-2),
            todayRestingHr: 80,
            yesterdayRestingHr: 60);

        Assert.Equal(1, await CreateSut().GenerateDueDigestsAsync(UtcNow));
    }

    /// <summary>
    /// Ordinary new readings inside the floor still skip: today's resting HR moved a couple of
    /// beats, which is exactly the continuously-uploading case the floor exists for.
    /// </summary>
    [Fact]
    public async Task StillSkipsInsideTheFloor_WhenNewDataIsOrdinary()
    {
        GivenPreviousSummary(UtcNow.AddMinutes(-5));
        SetupTodayAndYesterday(
            todayLandedAt: UtcNow.AddMinutes(-2),
            todayRestingHr: 64,
            yesterdayRestingHr: 62);

        Assert.Equal(0, await CreateSut().GenerateDueDigestsAsync(UtcNow));
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The one gate nothing waives. A summary generated from silence would read as "all quiet" when
    /// the truth is "not measuring" — the confusion this product exists to prevent.
    /// </summary>
    [Fact]
    public async Task NeverSummarisesSilence_EvenWhenAnAlertJustChanged()
    {
        GivenPreviousSummary(UtcNow.AddMinutes(-5));
        GivenAlerts(AnAlert(UtcNow.AddMinutes(-1), resolved: false));
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);

        Assert.Equal(0, await CreateSut().GenerateDueDigestsAsync(UtcNow));
    }

    [Fact]
    public async Task ThePrompt_CarriesUnresolvedAlerts_SoTheSummaryCannotContradictThem()
    {
        GivenAlerts(AnAlert(UtcNow.AddHours(-2), resolved: false));

        var prompt = await CapturedPrompt();

        Assert.Contains("--- Recent monitoring context ---", prompt);
        Assert.Contains("Unresolved alert (Orange, HeartRate", prompt);
    }

    [Fact]
    public async Task ThePrompt_CarriesNoMonitoringSection_ForACalmMember()
    {
        Assert.DoesNotContain("Recent monitoring context ---", await CapturedPrompt());
    }

    // ---- Questions ----

    [Fact]
    public void TheQuestionRationaleSchema_AsksForCaregiverLanguage_NotALabNote()
    {
        var description = typeof(DigestGenerationService.DigestAiResponse)
            .GetProperty(nameof(DigestGenerationService.DigestAiResponse.QuestionRationale))!
            .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!
            .Description;

        Assert.Contains("everyday sentence in a caregiver's words", description, StringComparison.Ordinal);
        Assert.DoesNotContain("prompted", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("in the readings", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoresTheProposedQuestion_WithTheReasonItWasAsked()
    {
        ReturnsQuestion("Has anything changed at home recently?", "Yesterday looked quieter than usual.");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.CardiMemberId == _memberId
            && q.Status == QuestionnaireStatus.Pending
            && q.TriggerContext == "Yesterday looked quieter than usual."
            && q.GeneratedAtUtc == UtcNow));
        await _unitOfWork.Received().SaveChangesAsync();
    }

    /// <summary>Stored encrypted, like everything else a family says about a member.</summary>
    [Fact]
    public async Task StoresTheQuestionEncrypted()
    {
        const string question = "Has anything changed at home recently?";
        ReturnsQuestion(question);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.QuestionText != question
            && PromptContextFactory.Encryption.Decrypt(q.QuestionText) == question));
    }

    [Fact]
    public async Task AsksNothing_WhenTheModelProposedNothing()
    {
        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>One open question at a time — a family that has not answered is not asked again.</summary>
    [Fact]
    public async Task AsksNothing_WhileAQuestionIsAlreadyWaiting()
    {
        ReturnsQuestion("Has anything changed at home recently?");
        _questionnaires.HasPendingAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>
    /// Measured from the asking, not the answering: declining to answer must not read as an
    /// invitation to ask again tomorrow.
    /// </summary>
    [Theory]
    [InlineData(3, false)]
    [InlineData(8, true)]
    public async Task RespectsTheIntervalBetweenQuestions(int daysSinceLastAsked, bool expectStored)
    {
        ReturnsQuestion("Has anything changed at home recently?");
        _questionnaires.GetLatestGeneratedAtAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(UtcNow.AddDays(-daysSinceLastAsked));

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(expectStored ? 1 : 0).AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>A question is a by-product of a summary worth keeping, not of a call being made.</summary>
    [Fact]
    public async Task AsksNothing_WhenTheSummaryItselfWasDiscarded()
    {
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Summary = "Here are the recent readings for their family.",
                Headline = "A settled night",
                Question = "Has anything changed at home recently?",
            });

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>
    /// CardiTrack is not a medical device, and "have you checked her blood pressure?" is a clinical
    /// instruction wearing a question mark. The summary is stored either way.
    /// </summary>
    [Theory]
    [InlineData("Have you checked her blood pressure today?")]
    [InlineData("Has her medication changed recently?")]
    [InlineData("Could you measure her pulse this evening?")]
    [InlineData("Has she had any new symptoms?")]
    [InlineData("Anything changed at home recently")]
    [InlineData("Most days there is nothing worth asking?")]
    public async Task DropsAQuestionThatShouldNeverBeAsked(string question)
    {
        ReturnsQuestion(question);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
        await _digests.Received(1).AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- Urgency ----

    [Theory]
    [InlineData("watch", DigestUrgency.Watch)]
    [InlineData("check-in", DigestUrgency.CheckIn)]
    [InlineData("concerning", DigestUrgency.Concerning)]
    [InlineData("act-now", DigestUrgency.ActNow)]
    public async Task StoresTheUrgencyTier_WhenItMatchesOneOfTheFour(string urgency, DigestUrgency expected)
    {
        ReturnsUrgency(urgency);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Urgency == expected), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DropsTheUrgencyTier_WhenItDoesNotMatchOneOfTheFour()
    {
        ReturnsUrgency("kind of concerning I guess");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Urgency == null), Arg.Any<CancellationToken>());
    }

    // ---- Question scope and expiry ----

    [Fact]
    public async Task StoresAPermanentQuestion_WithNoExpiry()
    {
        ReturnsQuestion("Does she have a pacemaker?", questionScope: "permanent");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.Scope == QuestionnaireScope.Permanent && q.ExpiresAtUtc == null));
    }

    [Fact]
    public async Task StoresATimeScopedQuestion_WithAnExpiryThirtyDaysOut()
    {
        ReturnsQuestion("Are you travelling this week?", questionScope: "time-scoped");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.Scope == QuestionnaireScope.TimeScoped && q.ExpiresAtUtc == UtcNow + TimeSpan.FromDays(30)));
    }

    // ---- How long a question stays worth asking ----
    //
    // A second clock, and genuinely different from the one above: ExpiresAtUtc is how long an answer
    // keeps informing prompts, AskableUntilUtc is how long the question is still worth putting in
    // front of anybody. Conflating them is what put "did he feel tired at all today?" on a
    // caregiver's screen at 07:15 the following morning.

    /// <summary>
    /// The end of the member's own day plus the grace, converted through their zone — not a fixed
    /// span from now. UtcNow is 10:30 in London on a mid-BST day, so local midnight + grace is
    /// 03:00 BST the next morning = 02:00 UTC.
    /// </summary>
    [Fact]
    public async Task StoresATimeScopedQuestion_AskableUntilTheEndOfTheMembersOwnDay()
    {
        ReturnsQuestion("Did he have visitors today?", questionScope: "time-scoped");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.AskableUntilUtc == new DateTime(2026, 8, 11, 2, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// A standing fact is as answerable next week as it is tonight, so nothing retires it — the
    /// same null the listing and the sweep both read as "never lapses".
    /// </summary>
    [Fact]
    public async Task StoresAPermanentQuestion_WithNoAskDeadline()
    {
        ReturnsQuestion("Does she have a pacemaker?", questionScope: "permanent");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.AskableUntilUtc == null));
    }

    /// <summary>
    /// Across the spring-forward night the wall-clock hours left in the local day are one hour
    /// longer than those hours in UTC. Adding that delta to utcNow would retire the question an
    /// hour late; converting the local end-of-day through the zone lands on the real instant.
    /// </summary>
    [Fact]
    public async Task AskDeadline_UsesTheZonesConversion_AcrossSpringForward()
    {
        // The day before clocks jump forward in London (01:00 GMT → 02:00 BST on 29 Mar 2026).
        // 10:30 GMT; local midnight + grace is 03:00 the next morning, which is already BST → 02:00 UTC.
        // A wall-clock delta would have said 03:00 UTC.
        var beforeSpringForward = new DateTime(2026, 3, 28, 10, 30, 0, DateTimeKind.Utc);
        SetupActivity(beforeSpringForward.AddMinutes(-30));
        ReturnsQuestion("Did he have visitors today?", questionScope: "time-scoped");

        await CreateSut().GenerateDueDigestsAsync(beforeSpringForward);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.AskableUntilUtc == new DateTime(2026, 3, 29, 2, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// The autumn reverse of <see cref="AskDeadline_UsesTheZonesConversion_AcrossSpringForward"/>:
    /// the wall-clock delta would retire an hour early; the zone conversion keeps the question
    /// askable through the repeated hour.
    /// </summary>
    [Fact]
    public async Task AskDeadline_UsesTheZonesConversion_AcrossFallBack()
    {
        // The day before clocks fall back in London (02:00 BST → 01:00 GMT on 25 Oct 2026).
        // 10:30 BST = 09:30 UTC; local midnight + grace is 03:00 GMT the next morning → 03:00 UTC.
        // A wall-clock delta would have said 02:00 UTC.
        var beforeFallBack = new DateTime(2026, 10, 24, 9, 30, 0, DateTimeKind.Utc);
        SetupActivity(beforeFallBack.AddMinutes(-30));
        ReturnsQuestion("Did he have visitors today?", questionScope: "time-scoped");

        await CreateSut().GenerateDueDigestsAsync(beforeFallBack);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.AskableUntilUtc == new DateTime(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// The arithmetic allows a question generated at 23:58 to lapse three hours and two minutes
    /// later, which is not a fair chance to see one. The floor is what the product owes a caregiver
    /// who happens to be asked something late.
    /// </summary>
    [Fact]
    public async Task GivesALateQuestionAFullWindow_RatherThanTheMinutesLeftInItsDay()
    {
        // 22:50 in London, so the local day plus the grace has only 4h10m left in it.
        var lateAtNight = new DateTime(2026, 8, 10, 21, 50, 0, DateTimeKind.Utc);
        SetupActivity(lateAtNight.AddMinutes(-2));
        ReturnsQuestion("Did he have visitors today?", questionScope: "time-scoped");

        await CreateSut().GenerateDueDigestsAsync(lateAtNight);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.AskableUntilUtc == lateAtNight.AddHours(6)));
    }

    [Fact]
    public async Task DefaultsToTimeScoped_WhenTheModelDidNotSayWhichScope()
    {
        ReturnsQuestion("Has anything changed at home recently?");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.Scope == QuestionnaireScope.TimeScoped));
    }

    // ---- Question gating: the gap-backed ceiling beats the ordinary floor ----

    /// <summary>A day since the last question is well inside the ordinary 7-day floor, but past
    /// the 12h gap-backed ceiling — proving the shorter path actually applies, not just that the
    /// interval happened to clear both.</summary>
    [Fact]
    public async Task GapBackedQuestion_IsAllowed_SoonerThanTheOrdinaryFloorWouldAllow()
    {
        ReturnsQuestion("Has anything changed at home recently?");
        GivenAlerts(AnAlert(UtcNow.AddHours(-2), resolved: false));
        _questionnaires.GetLatestGeneratedAtAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(UtcNow.AddDays(-1));

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    [Fact]
    public async Task GapBackedQuestion_StillWaitsOutTheTwelveHourCeiling()
    {
        ReturnsQuestion("Has anything changed at home recently?");
        GivenAlerts(AnAlert(UtcNow.AddHours(-2), resolved: false));
        _questionnaires.GetLatestGeneratedAtAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(UtcNow.AddHours(-6));

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>The exact same one-day interval that <see cref="GapBackedQuestion_IsAllowed_SoonerThanTheOrdinaryFloorWouldAllow"/>
    /// lets through — without a gap, it still waits for the ordinary floor.</summary>
    [Fact]
    public async Task SameIntervalWithoutAGap_StillWaitsForTheOrdinaryFloor()
    {
        ReturnsQuestion("Has anything changed at home recently?");
        _questionnaires.GetLatestGeneratedAtAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(UtcNow.AddDays(-1));

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>The gap check isn't alert-only — a Yellow+ automated observation counts too, the
    /// same definition MonitoringContextSource uses to decide whether the prompt itself mentions
    /// monitoring.</summary>
    [Fact]
    public async Task GapBackedQuestion_AlsoTriggersFromAYellowPlusAssessment_NotJustAnAlert()
    {
        ReturnsQuestion("Has anything changed at home recently?");
        _realtimeAssessments.GetSinceAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([new RealtimeAssessment
            {
                CardiMemberId = _memberId,
                Severity = AlertSeverity.Yellow,
                WindowEndUtc = UtcNow.AddHours(-1),
            }]);
        _questionnaires.GetLatestGeneratedAtAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(UtcNow.AddDays(-1));

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>A permanent question is a first ask of a new standing fact, not a repeat of an
    /// old one — it clears the pending-question gate alone, without waiting on the anti-fatigue
    /// floor an ordinary time-scoped question would be held to.</summary>
    [Fact]
    public async Task PermanentQuestion_IgnoresTheAntiFatigueFloor_WhenThereIsNoGap()
    {
        ReturnsQuestion("Does she have a pacemaker?", questionScope: "permanent");
        _questionnaires.GetLatestGeneratedAtAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(UtcNow.AddHours(-1));

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>
    /// The 12-hour gap path exists so a <em>different</em> question can close a live gap sooner.
    /// The same sentence landing twice in a day is the thing the ordinary week is there to stop,
    /// and a yellow observation does not waive that.
    /// </summary>
    [Fact]
    public async Task DoesNotReaskTheSameQuestion_EvenWhenAGapWouldAllowANewOne()
    {
        const string question = "Did Dad have any visitors yesterday?";
        ReturnsQuestion(question, "Yesterday looked quieter than usual.");
        GivenAlerts(AnAlert(UtcNow.AddHours(-2), resolved: false));
        GivenPreviousQuestion(question, UtcNow.AddHours(-17));

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    [Fact]
    public async Task DoesNotReaskTheSameQuestion_WhenOnlyPunctuationDiffers()
    {
        ReturnsQuestion("Did Dad have any visitors, yesterday?");
        GivenAlerts(AnAlert(UtcNow.AddHours(-2), resolved: false));
        GivenPreviousQuestion("Did Dad have any visitors yesterday?", UtcNow.AddHours(-17));

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>The skip control promises the question will not come back. Honour that even after
    /// the ordinary week has passed, otherwise "we won't ask it again" is a lie.</summary>
    [Fact]
    public async Task DoesNotReaskADismissedQuestion_EvenAfterTheOrdinaryFloor()
    {
        const string question = "Did Dad have any visitors yesterday?";
        ReturnsQuestion(question);
        GivenPreviousQuestion(question, UtcNow.AddDays(-8), QuestionnaireStatus.Dismissed);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>A standing fact already answered is not a new standing fact. The permanent path
    /// skips the week so a <em>first</em> ask can land; it must not loop the one they already
    /// answered.</summary>
    [Fact]
    public async Task DoesNotReaskAnAnsweredPermanentQuestion()
    {
        const string question = "Does she have a pacemaker?";
        ReturnsQuestion(question, questionScope: "permanent");
        GivenPreviousQuestion(
            question, UtcNow.AddDays(-8), QuestionnaireStatus.Answered, QuestionnaireScope.Permanent);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    [Fact]
    public async Task GapBackedQuestion_MayAskSomethingDifferent_SoonerThanTheOrdinaryFloor()
    {
        ReturnsQuestion("Has anything changed at home recently?", "Yesterday looked quieter than usual.");
        GivenAlerts(AnAlert(UtcNow.AddHours(-2), resolved: false));
        GivenPreviousQuestion("Did Dad have any visitors yesterday?", UtcNow.AddDays(-1));

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>
    /// A mechanical caption is worse than none. The question is still worth asking; the family
    /// just should not have to read a lab note under it.
    /// </summary>
    [Theory]
    [InlineData("The question was prompted by the reading that Dad had no visitors yesterday.")]
    [InlineData("The heart rate was slightly elevated today, reaching 100 bpm during the last hour.")]
    public async Task DropsAMechanicalRationale_ButStillAsksTheQuestion(string rationale)
    {
        ReturnsQuestion("Has anything changed at home recently?", rationale);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.TriggerContext == null
            && PromptContextFactory.Encryption.Decrypt(q.QuestionText)
                == "Has anything changed at home recently?"));
    }

    [Fact]
    public async Task StripsAnAskedBecausePrefix_RatherThanShowingItTwice()
    {
        ReturnsQuestion(
            "Has anything changed at home recently?",
            "Asked because yesterday looked quieter than usual.");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.TriggerContext == "Yesterday looked quieter than usual."));
    }

    // ---- Arrangement helpers for the sections above ----

    private void GivenPreviousSummary(DateTime generatedAt) =>
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today,
                GeneratedAtUtc = generatedAt,
            });

    /// <summary>
    /// Both alert reads the digest pipeline makes: the broad one it uses itself for the journal's
    /// day/week/month windows, and the unresolved one <c>MonitoringContextSource</c> uses for the
    /// digest's monitoring section. The second is derived from the same alerts with the filter and
    /// ordering the SQL query applies, so a test writes one list and both paths agree on it.
    /// </summary>
    private void GivenAlerts(params Alert[] alerts)
    {
        _alerts.GetByCardiMemberAsync(_memberId, Arg.Any<bool>()).Returns(alerts);
        _alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns(
            alerts.Where(a => a.IsActive && !a.IsResolved)
                .OrderByDescending(a => a.TriggeredDate)
                .ToList());
    }

    private void GivenLatestAssessment(AlertSeverity? severity, double score, DateTime generatedAt) =>
        _realtimeAssessments.GetLatestAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessment
            {
                CardiMemberId = _memberId,
                Severity = severity,
                HrDeviationScore = score,
                GeneratedAtUtc = generatedAt,
            });

    private Alert AnAlert(DateTime triggeredAt, bool resolved, DateTime? updatedAt = null) => new()
    {
        CardiMemberId = _memberId,
        AlertType = AlertType.HeartRate,
        Severity = AlertSeverity.Orange,
        Title = "Heart rate worth checking on",
        TriggeredDate = triggeredAt,
        IsResolved = resolved,
        UpdatedDate = updatedAt,
    };

    private void GivenPreviousQuestion(
        string question,
        DateTime askedAt,
        QuestionnaireStatus status = QuestionnaireStatus.Answered,
        QuestionnaireScope scope = QuestionnaireScope.TimeScoped)
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new MemberQuestionnaire
                {
                    CardiMemberId = _memberId,
                    QuestionText = PromptContextFactory.Encryption.Encrypt(question),
                    Status = status,
                    GeneratedAtUtc = askedAt,
                    Scope = scope,
                },
            ]);
        _questionnaires.GetLatestGeneratedAtAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(askedAt);
    }

    private void GivenAnsweredQuestion(
        string question, string answer, QuestionnaireScope scope = QuestionnaireScope.TimeScoped)
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new MemberQuestionnaire
                {
                    CardiMemberId = _memberId,
                    QuestionText = PromptContextFactory.Encryption.Encrypt(question),
                    AnswerText = PromptContextFactory.Encryption.Encrypt(answer),
                    Status = QuestionnaireStatus.Answered,
                    GeneratedAtUtc = UtcNow.AddDays(-1),
                    Scope = scope,
                },
            ]);
    }

    private void ReturnsQuestion(string question, string? rationale = null, string? questionScope = null) =>
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A settled night",
                Summary = "A settled day: steady heart rate and a good night's sleep.",
                Question = question,
                QuestionRationale = rationale,
                QuestionScope = questionScope,
            });

    private void ReturnsUrgency(string urgency) =>
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A settled night",
                Summary = "A settled day: steady heart rate and a good night's sleep.",
                Urgency = urgency,
            });

    private async Task<string> CapturedPrompt()
    {
        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.NotNull(prompt);
        return prompt;
    }
}
