using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using CardiTrack.Application.Diagnostics;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Services.PromptContext;
using CardiTrack.UnitTests.Observability;
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
[Collection(QuestionnaireTelemetryCollection.Name)]
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
    private readonly IMemberAiHoldRepository _holds = Substitute.For<IMemberAiHoldRepository>();
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();

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
        _unitOfWork.MemberAiHolds.Returns(_holds);

        // Defaults: one active London-anchored member whose data landed half an hour ago, and who
        // has never had a summary written — and whom the model has never failed to read.
        _holds.GetAsync(Arg.Any<Guid>(), Arg.Any<AiHoldPurpose>(), Arg.Any<CancellationToken>())
            .Returns((MemberAiHold?)null);
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
        // The insert reports whether a row landed. NSubstitute's Task<bool> default is false,
        // which would look like every write colliding; the ordinary path in this suite is a
        // successful insert.
        _digests.AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>()).Returns(true);
        // The family digest is two calls: MedGemma reads the day, the rewrite slot writes the
        // family's copy from that read alone. Both are stubbed by default so a test that cares
        // about neither still gets a stored summary.
        ReturnsClinicalRead();
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A settled night",
                Summary = "A settled day: steady heart rate and a good night's sleep.",
            });
    }

    /// <summary>
    /// Stubs the clinical half. Defaults to a read that carries a finding and nothing else, which
    /// is the shape the rewrite stub above answers.
    /// </summary>
    private void ReturnsClinicalRead(
        string finding = "Resting heart rate and sleep both sit within this member's usual range.",
        string urgency = "watch",
        string? actionBasis = null,
        string? questionTopic = null,
        string? questionScope = null) =>
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestClinicalAiResponse
            {
                Finding = finding,
                Urgency = urgency,
                ActionBasis = actionBasis,
                QuestionTopic = questionTopic,
                QuestionScope = questionScope,
            });

    private CardiMember Member() => new()
    {
        Id = _memberId,
        FirstName = "Margaret",
        LastName = "Doe",
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
        new(_unitOfWork, _medicalAi, _rewriteAi, PromptContextFactory.Composer(_unitOfWork),
            PromptContextFactory.Encryption, InertStatusLineGenerator.Create(),
            InertAdviseGenerator.Create(), NullLogger<DigestGenerationService>.Instance, new PassThroughWriteGuard());

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
        _medicalAi.GenerateStructuredAsync<StatusLineGenerationService.StatusClinicalAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StatusLineGenerationService.StatusClinicalAiResponse
            {
                Finding = "Activity sat well below the usual step total.",
            });
        _rewriteAi.GenerateStructuredAsync<StatusLineGenerationService.CurrentStatusAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StatusLineGenerationService.CurrentStatusAiResponse
            {
                Headline = "All steady",
                Message = "Steady today.",
            });
        var sut = new DigestGenerationService(
            _unitOfWork, _medicalAi, _rewriteAi, PromptContextFactory.Composer(_unitOfWork),
            PromptContextFactory.Encryption,
            new StatusLineGenerationService(
                _unitOfWork, _medicalAi, _rewriteAi, PromptContextFactory.Composer(_unitOfWork),
                NullLogger<StatusLineGenerationService>.Instance,
                new PassThroughWriteGuard()),
            InertAdviseGenerator.Create(),
            NullLogger<DigestGenerationService>.Instance, new PassThroughWriteGuard());

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
                PromptVersion = DigestGenerationService.CurrentPromptVersion,
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        // Appended, not overwritten: the earlier generation is the history behind this one.
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.LocalDate == Today && d.GeneratedAtUtc == UtcNow),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A summary written by a brief this service no longer sends is stale whatever its readings
    /// did. Rows from before the column existed read 0, which is the intended reading of them:
    /// they were written by a brief that chose a sex for the member.
    /// </summary>
    [Fact]
    public async Task Regenerates_WhenTheStoredSummaryCameFromAnOlderBrief()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today,
                // Newer than the readings, so every ordinary gate would stop here.
                GeneratedAtUtc = DataLandedAt.AddMinutes(1),
                Text = "A settled day.",
                PromptVersion = DigestGenerationService.CurrentPromptVersion - 1,
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
    }

    /// <summary>And a row from the current brief is left exactly as the gates left it.</summary>
    [Fact]
    public async Task Skips_WhenTheStoredSummaryIsFromTheCurrentBrief()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today,
                GeneratedAtUtc = DataLandedAt.AddMinutes(1),
                Text = "A settled day.",
                PromptVersion = DigestGenerationService.CurrentPromptVersion,
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
    }

    /// <summary>
    /// Every row this service writes carries the version that wrote it — the journals too, which
    /// never act on it, so a row's provenance does not depend on which gate reads it back.
    /// </summary>
    [Fact]
    public async Task StampsTheSummaryWithTheVersionThatWroteIt()
    {
        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.PromptVersion == DigestGenerationService.CurrentPromptVersion),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The other waiver, which the version cannot cover: a member whose sex was filled in after
    /// the summary was written, where the stored pronoun was nobody's mistake and is wrong anyway.
    /// </summary>
    [Fact]
    public async Task Regenerates_WhenTheStoredSummaryStatesASexTheRecordDoesNotBearOut()
    {
        _members.GetByIdAsync(_memberId).Returns(MemberWithNoSexOnFile());
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today,
                // Newer than the readings, so the ordinary gate would stop here — and stamped with
                // the current brief, so this turns on the copy rather than on the version.
                GeneratedAtUtc = DataLandedAt.AddMinutes(1),
                Text = "He slept well and his heart rate was steady.",
                PromptVersion = DigestGenerationService.CurrentPromptVersion,
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
    }

    /// <summary>
    /// And the gate still holds for a stored summary whose sex the record bears out — this member
    /// is on file as female, so "her" is the word the code would have chosen itself.
    /// </summary>
    [Fact]
    public async Task Skips_WhenTheStoredSummarysSexIsTheRecordsOwn()
    {
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today,
                GeneratedAtUtc = DataLandedAt.AddMinutes(1),
                Text = "She slept well and her heart rate was steady.",
                PromptVersion = DigestGenerationService.CurrentPromptVersion,
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
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
                PromptVersion = DigestGenerationService.CurrentPromptVersion,
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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
                PromptVersion = DigestGenerationService.CurrentPromptVersion,
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
                PromptVersion = DigestGenerationService.CurrentPromptVersion,
            });
        SetupActivity(UtcNow.AddMinutes(-2));

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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
                PromptVersion = DigestGenerationService.CurrentPromptVersion,
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
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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
                PromptVersion = DigestGenerationService.CurrentPromptVersion,
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
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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
                PromptVersion = DigestGenerationService.CurrentPromptVersion,
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
            + "last night's sleep belongs on this row and has not arrived",
            prompt);
        Assert.Contains("\"steps\": 5000", prompt);
        Assert.Contains("[INPUT DATA]", prompt);
        Assert.Contains("```json", prompt);
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
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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
    [InlineData("Write a loved one's family their summary of the day.")]
    // Re-wrapped: the check flattens whitespace, so a differently broken echo still matches.
    [InlineData("Use plain,\n  reassuring language.\nNever diagnose.")]
    [InlineData("Respond with: summary — the summary itself, 2-4 sentences.")]
    [InlineData("   ")]
    public async Task DiscardsTheSummary_WhenTheModelEchoesItsInstructionsOrSaysNothing(string reply)
    {
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A bedroom move",
                Summary = "She moved bedrooms last week.",
            });

        using var capture = new QuestionnaireMetricCapture();

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
        Assert.Contains(capture.Longs, m => m.Instrument == "questionnaire.digest.recited" && m.Value == 1);
        Assert.DoesNotContain(capture.Longs, m => m.Instrument == "questionnaire.digest.informed");
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
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A shorter night",
                Summary = "She moved bedrooms last week, so last night was unsettled "
                    + "and her resting rate sat above usual.",
            });

        using var capture = new QuestionnaireMetricCapture();

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Text.Contains("resting rate", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        Assert.Contains(capture.Longs, m => m.Instrument == "questionnaire.digest.informed" && m.Value == 1);
        Assert.DoesNotContain(capture.Longs, m => m.Instrument == "questionnaire.digest.recited");
    }

    /// <summary>
    /// ComposeAsync omits a source that threw. The second VisibleFacts read can still succeed;
    /// recap and informed have to follow the prompt, not the extra read.
    /// </summary>
    [Fact]
    public async Task DoesNotCountTheDigestAsInformed_WhenTheQuestionnaireSectionWasOmitted()
    {
        var answered = new MemberQuestionnaire
        {
            CardiMemberId = _memberId,
            QuestionText = PromptContextFactory.Encryption.Encrypt("Has anything changed at home recently?"),
            AnswerText = PromptContextFactory.Encryption.Encrypt("She moved bedrooms last week."),
            Status = QuestionnaireStatus.Answered,
            GeneratedAtUtc = UtcNow.AddDays(-1),
            Scope = QuestionnaireScope.TimeScoped,
        };
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new InvalidOperationException("questionnaire source failed"),
                _ => [answered]);

        using var capture = new QuestionnaireMetricCapture();

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        Assert.DoesNotContain(capture.Longs, m => m.Instrument == "questionnaire.digest.informed");
        Assert.DoesNotContain(capture.Longs, m => m.Instrument == "questionnaire.digest.recited");
    }

    /// <summary>
    /// AddAsync is INSERT ON CONFLICT DO NOTHING. A colliding run still reaches the informed
    /// counter, but nothing was stored, so the family never read a digest this pass informed.
    /// </summary>
    [Fact]
    public async Task DoesNotCountTheDigestAsInformed_WhenTheInsertWasAbsorbed()
    {
        GivenAnsweredQuestion(
            "Has anything changed at home recently?", "She moved bedrooms last week.");
        _digests.AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>()).Returns(false);

        using var capture = new QuestionnaireMetricCapture();

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        Assert.DoesNotContain(capture.Longs, m => m.Instrument == "questionnaire.digest.informed");
    }

    /// <summary>
    /// A colliding insert must not ask a question on the strength of a digest nobody stored.
    /// Two overlapping passes can both see no pending question; only the winning insert asks.
    /// </summary>
    [Fact]
    public async Task DoesNotAsk_WhenTheInsertWasAbsorbed()
    {
        ReturnsQuestion("Has anything changed at home recently?");
        _digests.AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>()).Returns(false);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// The second VisibleFacts read is bookkeeping. A transient failure after ComposeAsync
    /// already included the section must not discard the digest.
    /// </summary>
    [Fact]
    public async Task StoresTheDigest_WhenTheFamilyFactsReloadFails()
    {
        var answered = new MemberQuestionnaire
        {
            CardiMemberId = _memberId,
            QuestionText = PromptContextFactory.Encryption.Encrypt("Has anything changed at home recently?"),
            AnswerText = PromptContextFactory.Encryption.Encrypt("She moved bedrooms last week."),
            Status = QuestionnaireStatus.Answered,
            GeneratedAtUtc = UtcNow.AddDays(-1),
            Scope = QuestionnaireScope.TimeScoped,
        };
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(
                _ => [answered],
                _ => throw new InvalidOperationException("reload failed"));

        using var capture = new QuestionnaireMetricCapture();

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(capture.Longs, m => m.Instrument == "questionnaire.digest.informed");
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
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
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
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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
    [InlineData("A quiet and steady day so far today")]
    [MemberData(nameof(ParrotedHeadlineLabels))]
    public async Task StoresTheSummaryWithoutAHeadline_WhenTheHeadlineIsUnusable(string headline)
    {
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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

    public static IEnumerable<object[]> ParrotedHeadlineLabels() =>
        DigestGenerationService.ParrotedHeadlines.Select(h => new object[] { h });

    /// <summary>
    /// The headline from the card that prompted all of this. The read compared a daytime
    /// heart-rate peak against the member's much lower resting baseline; the family's card was
    /// titled "Elevated resting heart rate", over a summary saying the rate ran slightly higher
    /// than usual. A title is read on its own, and that one is a clinician's finding — the
    /// register the caregiver block rules out.
    /// </summary>
    [Theory]
    [InlineData("Elevated resting heart rate")]
    [InlineData("Abnormal overnight readings")]
    [InlineData("A deviation from the usual")]
    public async Task StoresTheSummaryWithoutAHeadline_WhenTheHeadlineIsClinicSpeak(string headline)
    {
        ReturnsClinicalRead("Heart rate ran above this member's usual resting rate yesterday evening.");
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = headline,
                Summary = "Her heart rate ran a little higher than usual yesterday evening.",
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Headline == null), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The plain-English way of saying the same thing is what the brief asks for, and must survive.
    /// </summary>
    [Fact]
    public async Task KeepsAHeadlineThatSaysItInEverydayWords()
    {
        ReturnsClinicalRead("Heart rate ran above this member's usual resting rate yesterday evening.");
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "Heart rate a little higher",
                Summary = "Her heart rate ran a little higher than usual yesterday evening.",
            });

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Headline == "Heart rate a little higher"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The other half of that card: a read that never mentioned oxygen came back as "breathing and
    /// oxygen levels remained stable". The family was told a reading had been taken and was fine.
    /// Nothing is stored — the previous card, which was true, stays on screen.
    /// </summary>
    [Fact]
    public async Task DiscardsTheSummary_WhenItNamesAReadingTheReadDidNot()
    {
        ReturnsClinicalRead("Breathing rate while asleep was slightly higher than usual.");
        ReturnsSummary("Her breathing and oxygen levels remained stable overnight.");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The rewrite slot is shown no member context, so a sex in its copy is one it chose. This
    /// member is on file as female; a summary about "he" is about someone else.
    /// </summary>
    [Fact]
    public async Task DiscardsTheSummary_WhenItStatesASexTheRecordDoesNotBearOut()
    {
        ReturnsSummary("He slept well and his heart rate was steady.");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// And the copy the brief now asks for: tokens where the pronouns go, resolved from the record
    /// on the way out. The member is Margaret Doe, female — so "she", written by this code and not
    /// guessed by a model that was never told.
    /// </summary>
    [Fact]
    public async Task ResolvesThePronounTokensFromTheMembersRecord()
    {
        ReturnsSummary(
            $"{NamePlaceholder.Token} slept well, and {PronounPlaceholder.Possessive} heart rate "
            + $"was steady. Sit with {PronounPlaceholder.Object} this evening.");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Text ==
                "Margaret slept well, and her heart rate was steady. Sit with her this evening."),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The summary is not the whole card. A member could be handed a neutral summary and a
    /// headline or suggestion that picked a sex for them — both optional fields, both stored
    /// unchanged before this check, and both read as fact by a family.
    /// </summary>
    [Fact]
    public async Task StoresTheSummaryWithoutAHeadline_WhenTheHeadlineStatesAnUnsupportedSex()
    {
        _members.GetByIdAsync(_memberId).Returns(MemberWithNoSexOnFile());
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "His quietest day this week",
                Summary = "A quiet, steady day.",
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Headline == null && d.Text == "A quiet, steady day."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoresTheSummaryWithoutASuggestion_WhenTheSuggestionStatesAnUnsupportedSex()
    {
        _members.GetByIdAsync(_memberId).Returns(MemberWithNoSexOnFile());
        ReturnsSuggestion("Walk with her after lunch if she is up to it.");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == null && d.Text == "A quiet, steady day."),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A sex that matches the record is left alone here as everywhere else: the member is on file
    /// as female, so the suggestion is one a family can read without being told anything untrue.
    /// </summary>
    [Fact]
    public async Task KeepsASuggestionWhoseSexTheRecordBearsOut()
    {
        ReturnsSuggestion("Walk with her after lunch if she is up to it.");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == "Walk with her after lunch if she is up to it."),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The member as every row created before M1-04 asked for sex still holds them.</summary>
    private CardiMember MemberWithNoSexOnFile()
    {
        var member = Member();
        member.Gender = Gender.PreferNotToSay;
        return member;
    }

    /// <summary>
    /// A title is a claim about the day in three words, and it is the line a family reads first.
    /// "Oxygen levels stable" over a read that never mentioned oxygen is the summary's own
    /// invention, moved to the one field that was not being grounded.
    /// </summary>
    [Fact]
    public async Task StoresTheSummaryWithoutAHeadline_WhenTheHeadlineNamesAReadingTheReadDidNot()
    {
        ReturnsClinicalRead("Heart rate and sleep both sit within this member's usual range.");
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "Oxygen levels stable",
                Summary = "A settled day: steady heart rate and a good night's sleep.",
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Headline == null), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The question topic travels in the text the rewrite is sent, but it is a subject to ask the
    /// family about, not a reading anyone took — so it cannot vouch for a measurement the summary
    /// claims. Grounding on it would have let this summary through.
    /// </summary>
    [Fact]
    public async Task DiscardsTheSummary_WhenOnlyTheQuestionTopicNamedTheReading()
    {
        ReturnsClinicalRead(
            "Heart rate sits within this member's usual range.",
            questionTopic: "what their evenings and sleep usually look like");
        ReturnsSummary("Her sleep was shorter than usual last night.");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
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
        string? clinicalPrompt = null;
        string? rewritePrompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
            Arg.Do<string>(p => clinicalPrompt = p), Arg.Any<CancellationToken>());
        await _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Do<string>(p => rewritePrompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);
        Assert.NotNull(clinicalPrompt);
        Assert.NotNull(rewritePrompt);

        var echoes = (string[])typeof(DigestGenerationService)
            .GetField("InstructionEchoes", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        // Both halves, because the guard runs on the summary and the summary is written from the
        // clinical read — so a phrase out of either brief can reach a caregiver, the clinical one
        // by travelling through the read the rewrite is handed.
        var lines = clinicalPrompt.Split('\n')
            .Concat(rewritePrompt.Split('\n'))
            .Select(l => l.Trim())
            .ToList();

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
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);
        Assert.NotNull(prompt);

        Assert.Contains($"Yesterday ({Today.AddDays(-1)}, complete day)", prompt);
        // Today's label also carries the clock — see DigestDayProgress and the tests below for why
        // "partial" on its own was not enough. Asserted in two halves so those words are pinned
        // without this test also owning the wording of the clock phrase.
        Assert.Contains($"Today so far ({Today}, 10:30 local", prompt);
        Assert.Contains(
            "still in progress — activity totals are partial; "
            + "last night's sleep belongs on this row and has not arrived",
            prompt);
        Assert.Contains("\"steps\": 3835", prompt);
        Assert.Contains("\"steps\": 3442", prompt);
        Assert.Contains("\"date\":", prompt);

        var days = FencedReadings(prompt);
        Assert.Equal(2, days.Count);
        Assert.Contains("Yesterday", days[0]!["day"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(3835, days[0]!["steps"]!.GetValue<int>());
        Assert.Contains("Today so far", days[1]!["day"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(3442, days[1]!["steps"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheReadingsAreOrderedOldestFirst_AndTheHeaderSaysSo()
    {
        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
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
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);
        Assert.NotNull(prompt);
        return prompt;
    }

    private static JsonArray FencedReadings(string prompt)
    {
        var open = prompt.IndexOf("```json", StringComparison.Ordinal);
        Assert.True(open >= 0, "prompt has no JSON fence");
        var start = open + "```json".Length;
        var close = prompt.IndexOf("```", start, StringComparison.Ordinal);
        Assert.True(close > start, "JSON fence is unclosed");
        return JsonNode.Parse(prompt[start..close].Trim())!.AsArray();
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
            "- Last night: 3.6 hours of sleep (usual 7.0) — below the 7-8 hours recommended at their age (NSF), "
            + "and well short of their usual.",
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
                    // Inside the 7-8 hours recommended at 78 as well as near their usual: 6.7 hours,
                    // which this used, is now named against the range (decision 2026-09-25).
                    SleepMinutes = 450, CreatedDate = DataLandedAt,
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

        Assert.Contains("\"spo2_average\": 96.4", prompt);
        Assert.Contains("\"breathing_rate\": 14.2", prompt);
        Assert.Contains("\"deep\": 60", prompt);
        Assert.Contains("\"light\": 200", prompt);
        Assert.Contains("\"rem\": 80", prompt);
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

        Assert.Contains("\"steps\": 4350", prompt);
        Assert.Contains("\"active_minutes\": 42", prompt);
        Assert.Contains("\"resting_heart_rate\": 71", prompt);
        Assert.Contains("\"avg_heart_rate\": 84", prompt);
        Assert.Contains("\"max_heart_rate\": 108", prompt);
        Assert.Contains("\"spo2_average\": 96.4", prompt);
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
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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
        ReturnsClinicalRead("Steps today sit close to this member's usual.");
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
        ReturnsClinicalRead("Yesterday's steps were well above today's, which is early yet.");
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
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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
        ReturnsSuggestion("Remind Mum to take her evening medication if she hasn't yet");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestion == "Remind Mum to take her evening medication if she hasn't yet"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// On the rewrite half, which is where the boundary belongs now: the clinical read may name a
    /// condition, and this is the brief that decides the family is not told one.
    /// </summary>
    [Fact]
    public async Task AsksForASuggestionThatNeverNamesACondition()
    {
        var prompt = await CapturedRewritePrompt();

        Assert.Contains("never name or guess at a medical condition", prompt);
        Assert.Contains("never worded as something the", prompt);
        Assert.Contains("never carry the name of a condition into what you write", prompt);
    }

    /// <summary>
    /// The prompt asks for suggestions the readings earned, and carries no example that could be
    /// returned as one — an example beside the field is what the model reached for before.
    /// </summary>
    [Fact]
    public async Task AsksForSuggestionsTheReadingsEarned_AndOffersNoExampleToCopy()
    {
        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.NotNull(prompt);
        // The specificity rule sits with the half that can see the readings — a rewrite handed
        // only a clinical read cannot judge whether a suggestion answers them.
        Assert.Contains("must answer something in the readings or computed observations", prompt);
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

    // ---- A read the model cannot finish holds the member ----

    /// <summary>
    /// A structured reply that fills the output ceiling is the model looping inside the reply
    /// grammar, and the same prompt loops the same way on the next pass. The pass records a hold
    /// instead of writing anything — no rewrite call is spent on a read that never finished —
    /// and the member's summary count is simply zero, not an exception out of the loop.
    /// </summary>
    [Fact]
    public async Task TruncatedClinicalRead_HoldsTheMember_AndWritesNoSummary()
    {
        GivenTruncatedClinicalRead();

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _holds.Received(1).UpsertAsync(
            Arg.Is<MemberAiHold>(h =>
                h.CardiMemberId == _memberId
                && h.Purpose == AiHoldPurpose.FamilyDigest
                && h.ConsecutiveFailures == 1
                && h.LastFailedAtUtc == UtcNow
                && h.HeldUntilUtc == UtcNow.AddHours(2)
                && h.Reason == "truncated"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The hold is checked before every other probe and yields to none of the waivers. An alert
    /// raised since the last summary is the strongest reason the floor has to regenerate, and it
    /// still does not: the read that would describe the alert is the read that cannot finish.
    /// </summary>
    [Fact]
    public async Task HeldMember_IsSkippedBeforeAnyModelCall_EvenWhenAnAlertWouldWaiveTheFloor()
    {
        GivenPreviousSummary(UtcNow.AddHours(-3));
        GivenAlerts(AnAlert(triggeredAt: UtcNow.AddMinutes(-2), resolved: false));
        GivenHold(consecutiveFailures: 1, heldUntil: UtcNow.AddMinutes(1));

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive()
            .GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _digests.DidNotReceive().GetLatestAsync(
            Arg.Any<Guid>(), Arg.Any<DigestAudience>(), Arg.Any<CancellationToken>());
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The hold ends with the first reply that finishes, whatever its count had climbed to: the
    /// count is of failures in a row, and a finished read broke the row.
    /// </summary>
    [Fact]
    public async Task ExpiredHold_IsCleared_ByAReadThatFinishes()
    {
        GivenHold(consecutiveFailures: 2, heldUntil: UtcNow.AddMinutes(-1));

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _holds.Received(1).ClearAsync(_memberId, AiHoldPurpose.FamilyDigest, Arg.Any<CancellationToken>());
        await _holds.DidNotReceive().UpsertAsync(Arg.Any<MemberAiHold>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExpiredHold_ThenAnotherUnfinishedRead_LengthensTheHold()
    {
        GivenHold(consecutiveFailures: 2, heldUntil: UtcNow.AddMinutes(-1));
        GivenTruncatedClinicalRead();

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _holds.Received(1).UpsertAsync(
            Arg.Is<MemberAiHold>(h => h.ConsecutiveFailures == 3 && h.HeldUntilUtc == UtcNow.AddHours(8)),
            Arg.Any<CancellationToken>());
        await _holds.DidNotReceive().ClearAsync(
            Arg.Any<Guid>(), Arg.Any<AiHoldPurpose>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The common path pays one read and no write: a member who was never held is never cleared.</summary>
    [Fact]
    public async Task UnheldMember_CostsTheHoldTableOnlyARead()
    {
        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _holds.Received(1).GetAsync(_memberId, AiHoldPurpose.FamilyDigest, Arg.Any<CancellationToken>());
        await _holds.DidNotReceive().ClearAsync(
            Arg.Any<Guid>(), Arg.Any<AiHoldPurpose>(), Arg.Any<CancellationToken>());
        await _holds.DidNotReceive().UpsertAsync(Arg.Any<MemberAiHold>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Any other model failure keeps its existing shape — the loop's log-and-continue — and
    /// leaves the hold table alone: the hold is for a reply that did not finish, not for a host
    /// that did not answer.
    /// </summary>
    [Fact]
    public async Task OtherModelFailures_DoNotHoldTheMember()
    {
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("MedGemma generate_structured failed: HTTP 503"));

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _holds.DidNotReceive().UpsertAsync(Arg.Any<MemberAiHold>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 12)]
    [InlineData(40, 12)]
    public void HoldFor_DoublesPerConsecutiveFailure_UpToTheCap(int consecutiveFailures, int hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), DigestGenerationService.HoldFor(consecutiveFailures));

    private void GivenTruncatedClinicalRead() =>
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AiReplyTruncatedException(
                "MedGemma generate_structured stopped at the token budget rather than finishing",
                outputTokens: 2048, maxOutputTokens: 2048, inputTokens: 1869, contextTokens: 8192));

    private void GivenHold(int consecutiveFailures, DateTime heldUntil) =>
        _holds.GetAsync(_memberId, AiHoldPurpose.FamilyDigest, Arg.Any<CancellationToken>())
            .Returns(new MemberAiHold
            {
                CardiMemberId = _memberId,
                Purpose = AiHoldPurpose.FamilyDigest,
                ConsecutiveFailures = consecutiveFailures,
                HeldUntilUtc = heldUntil,
                LastFailedAtUtc = heldUntil.AddHours(-2),
                Reason = "truncated",
            });

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
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
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

        using var capture = new QuestionnaireMetricCapture();

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.CardiMemberId == _memberId
            && q.Status == QuestionnaireStatus.Pending
            && q.TriggerContext == "Yesterday looked quieter than usual."
            && q.GeneratedAtUtc == UtcNow));
        await _unitOfWork.Received().SaveChangesAsync();
        Assert.Contains(capture.Longs, m =>
            m.Instrument == "questionnaire.asked"
            && m.Value == 1
            && m.Tags.GetValueOrDefault(QuestionnaireTelemetry.ScopeTag) as string == "timescoped");
    }

    /// <summary>
    /// A question is put to the family in as many words as the summary is. This member's sex is
    /// not on file, so "he" is a guess — and the question is the one piece of copy on this path
    /// that a family is asked to answer.
    /// </summary>
    [Fact]
    public async Task AsksNothing_WhenTheQuestionStatesAnUnsupportedSex()
    {
        _members.GetByIdAsync(_memberId).Returns(MemberWithNoSexOnFile());
        ReturnsQuestion("Did he have visitors today?");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>
    /// The caption under the question is family-facing copy from the same reply, and was the one
    /// field on this path that checked neither rule. The question survives without it — that is
    /// what a dropped rationale has always meant.
    /// </summary>
    [Fact]
    public async Task StoresTheQuestionWithoutACaption_WhenTheRationaleStatesAnUnsupportedSex()
    {
        _members.GetByIdAsync(_memberId).Returns(MemberWithNoSexOnFile());
        ReturnsQuestion("Were there visitors today?", "His evenings are usually busier than this.");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.TriggerContext == null));
    }

    /// <summary>
    /// This case is now unreachable on the digest path, and the test says so rather than being
    /// deleted quietly. It used to blank the member's name, because that is the only way a pronoun
    /// token survives resolution: PronounPlaceholder falls back to the member's own name when sex
    /// is not stated, so an unresolvable token needs no sex <em>and</em> no name. From 2026-09-23 a
    /// member with no name never reaches the rewrite at all — there is nothing to redact the
    /// clinical read against, so nothing crosses and no question is composed to drop a caption
    /// from.
    /// </summary>
    /// <remarks>
    /// The caption guard it covered is still live and still worth having — it is the same check
    /// every other surface runs, and the next path to compose a rationale may not have this one's
    /// earlier exit. Its sibling above, which drops a caption stating a sex the record does not
    /// bear out, exercises the same guard on a case that is still reachable.
    /// </remarks>
    [Fact]
    public async Task NoQuestionIsComposedAtAll_ForAMemberWithNoNameToRedactAgainst()
    {
        var member = MemberWithNoSexOnFile();
        member.FirstName = string.Empty;
        member.LastName = null;
        _members.GetByIdAsync(_memberId).Returns(member);
        ReturnsQuestion(
            "Were there visitors today?",
            $"{PronounPlaceholder.Possessive} evenings are usually busier than this.");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
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

    /// <summary>
    /// DPIA row A20 as a test: the rewrite slot leaves the estate, so it is handed the clinical
    /// read and nothing else. No name, no age, no readings, no monitoring section, no family
    /// answers — the <c>DeidentifiedFindings</c> parameter makes that true by construction, and
    /// this pins it against a future edit that reaches for the member context because it was
    /// convenient.
    /// </summary>
    [Fact]
    public async Task TheRewritePrompt_CarriesTheClinicalReadAndNothingAboutTheMember()
    {
        GivenAnsweredQuestion("Does Margaret enjoy gardening", "Yes");
        ReturnsClinicalRead("Overnight breathing sat above this member's usual on a still day.");

        var prompt = await CapturedRewritePrompt();

        Assert.Contains("--- Clinical read to write from ---", prompt);
        Assert.Contains("Overnight breathing sat above this member's usual on a still day.", prompt);
        Assert.DoesNotContain("Margaret", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Age:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("--- Recent activity", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("--- Usual pattern", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(
            QuestionnaireAnswersContextSource.SectionLabel, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(
            MonitoringContextSource.SectionLabel, prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The clinical read decides whether there is anything worth asking. A rewrite that returns a
    /// question over a read that named no topic is inventing one — and would otherwise be stored
    /// as time-scoped, which is what <c>ParseScope</c> makes of a null scope.
    /// </summary>
    [Fact]
    public async Task AsksNothing_WhenTheClinicalReadNamedNoTopic()
    {
        ReturnsClinicalRead("Heart rate and sleep both sit within this member's usual range.");
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Summary = "A settled day: steady heart rate and a good night's sleep.",
                Headline = "A settled night",
                Question = "Has anything changed at home recently?",
                QuestionRationale = "It helps to know what their days look like.",
            });

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
        await _questionnaires.DidNotReceive().AddAsync(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>A question is a by-product of a summary worth keeping, not of a call being made.</summary>
    [Fact]
    public async Task AsksNothing_WhenTheSummaryItselfWasDiscarded()
    {
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Summary = "Write a loved one's family their summary of the day.",
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
        ReturnsQuestion("Did she have visitors today?", questionScope: "time-scoped");

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
        ReturnsQuestion("Did she have visitors today?", questionScope: "time-scoped");

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
        ReturnsQuestion("Did she have visitors today?", questionScope: "time-scoped");

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
        ReturnsQuestion("Did she have visitors today?", questionScope: "time-scoped");

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

    /// <summary>
    /// A volunteered standing fact was never asked. Its canned heading must not gag a later
    /// digest proposal that happens to use the same wording.
    /// </summary>
    [Fact]
    public async Task StillAsksTheCannedVolunteerWording_WhenAFamilyOfferedFactAlreadyUsesIt()
    {
        const string canned = "What should we know about them?";
        ReturnsQuestion(canned);
        GivenPreviousQuestion(
            canned, UtcNow.AddDays(-8), QuestionnaireStatus.Answered,
            QuestionnaireScope.Permanent, QuestionnaireOrigin.Family);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Any<MemberQuestionnaire>());
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
                PromptVersion = DigestGenerationService.CurrentPromptVersion,
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
        QuestionnaireScope scope = QuestionnaireScope.TimeScoped,
        QuestionnaireOrigin origin = QuestionnaireOrigin.Digest)
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
                    Origin = origin,
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

    /// <summary>
    /// A question needs both halves: the clinical read decides there is a topic worth asking about
    /// and how long the answer stays true, and the rewrite puts it in a family's words.
    /// </summary>
    private void ReturnsQuestion(string question, string? rationale = null, string? questionScope = null)
    {
        ReturnsClinicalRead(questionTopic: "what their evenings usually look like", questionScope: questionScope);
        _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A settled night",
                Summary = "A settled day: steady heart rate and a good night's sleep.",
                Question = question,
                QuestionRationale = rationale,
            });
    }

    /// <summary>Urgency is the clinical read's judgement — the rewrite is not shown the readings.</summary>
    private void ReturnsUrgency(string urgency) => ReturnsClinicalRead(urgency: urgency);

    /// <summary>
    /// The prompt MedGemma is sent: member context, the usual pattern, the interpretation signals
    /// and the readings. Every expectation about what the model is *told about the member* belongs
    /// here — see <see cref="CapturedRewritePrompt"/> for the other half.
    /// </summary>
    private async Task<string> CapturedPrompt()
    {
        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.NotNull(prompt);
        return prompt;
    }

    /// <summary>
    /// The prompt the rewrite slot is sent: the voice, the placeholder and the clinical read — and
    /// nothing about the member. What it must <em>not</em> contain is the point of most
    /// expectations against it.
    /// </summary>
    private async Task<string> CapturedRewritePrompt()
    {
        string? prompt = null;
        await _rewriteAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.NotNull(prompt);
        return prompt;
    }

    /// <summary>
    /// The family digest's crossing shipped with #507 and never redacted at all — it flattened the
    /// clinical read and sent it, unlike the status line, the trend, the journals and both alert
    /// paths, which all swap the name out. <c>DemographicsContextSource</c> serves
    /// <c>PromptPurpose.All</c>, so this prompt is given the decrypted caregiver notes and MedGemma
    /// can repeat a name out of them. It now redacts, and refuses outright when there is no name to
    /// redact against — <c>NamePlaceholder.Redact</c> hands the text straight back in that case, so
    /// proceeding would be the unredacted crossing wearing a guard's clothes.
    /// </summary>
    [Fact]
    public async Task TheDigestDoesNotCrossToTheRewriteSlot_WhenThereIsNoNameToRedactAgainst()
    {
        var member = Member();
        member.FirstName = "   ";
        member.LastName = null;
        _members.GetByIdAsync(_memberId).Returns(member);

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// And when there is one, the name does not reach the Rewrite slot: the clinical read crosses
    /// with it swapped for the placeholder, which is the boundary DPIA A20 describes.
    /// </summary>
    [Fact]
    public async Task TheDigestsClinicalRead_CrossesWithTheNameSwappedForThePlaceholder()
    {
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestClinicalAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestClinicalAiResponse
            {
                Finding = "Margaret walked less than usual today.",
                Urgency = "watch",
            });

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        var crossed = (string)_rewriteAi.ReceivedCalls()
            .First(c => c.GetArguments()[0] is string)
            .GetArguments()[0]!;
        Assert.DoesNotContain("Margaret", crossed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(NamePlaceholder.Token, crossed, StringComparison.Ordinal);
    }

}
