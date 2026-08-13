using System.Globalization;
using System.Reflection;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the summary due-ness rules: recomputed whenever the member's readings have moved since
/// their last summary but no more often than the regeneration floor, never while paused, never
/// from silence — and one member's failure never costs another family theirs. Every generation is
/// appended, so a day accumulates history rather than being overwritten.
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
    private readonly IMemberQuestionnaireRepository _questionnaires =
        Substitute.For<IMemberQuestionnaireRepository>();
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();

    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    /// <summary>05:30 UTC on a BST date — 06:30 in London, so the member's local day is the 10th.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 10, 5, 30, 0, DateTimeKind.Utc);

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
        // A calm member by default: no alerts to change state, nothing already asked.
        _alerts.GetByCardiMemberAsync(_memberId, Arg.Any<bool>()).Returns([]);
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>()).Returns([]);
        _questionnaires.HasPendingAsync(_memberId, Arg.Any<CancellationToken>()).Returns(false);
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
    private void SetupActivity(DateTime landedAt)
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
                    CreatedDate = landedAt,
                },
            ]);
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
            PromptContextFactory.Encryption, NullLogger<DigestGenerationService>.Instance);

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
                GeneratedAtUtc = DataLandedAt.AddMinutes(-5),
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
                GeneratedAtUtc = UtcNow.AddMinutes(-45),
            });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
    }

    /// <summary>
    /// The cost bound that lets the job run quarter-hourly. A worn device uploads on nearly every
    /// pass, so "data has moved" alone would mean an inference and a history row every fifteen
    /// minutes — the floor is what decouples how often the job runs from how many summaries a
    /// member accumulates.
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
        // Inside the floor the readings are never read either — the skip costs one indexed lookup,
        // not a date-range scan, which is what makes a mostly-skipping pass cheap.
        await _activityLogs.DidNotReceive().GetByCardiMemberAndDateRangeAsync(
            _memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>());
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
                GeneratedAtUtc = UtcNow.AddMinutes(-20),
            });
        SetupActivity(UtcNow.AddMinutes(-2));

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
    }

    /// <summary>
    /// The floor bounds regeneration, never the first summary: a member who has been quiet and
    /// starts uploading again is caught by the very next pass, which is the freshness the
    /// quarter-hourly cadence was bought for.
    /// </summary>
    [Fact]
    public async Task Generates_ImmediatelyForAMemberWithNoSummaryYet_WhateverTheFloor()
    {
        SetupActivity(UtcNow.AddMinutes(-1));

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
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
        Assert.Contains(
            $"Today so far ({Today}, still in progress — activity totals are partial; "
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
        // 05:30 UTC is still the 10th, so the described day doesn't move — only the zone did.
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.LocalDate == Today), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The Member Detail screen renders the stored summary verbatim, so a reply that is really the
    /// brief read back must never reach the database — a caregiver seeing the prompt is worse than
    /// a caregiver seeing the "nothing to say yet" copy.
    /// </summary>
    [Theory]
    [InlineData("You are summarising a loved one's recent heart health data for a "
                + "non-medical family member. Use plain, reassuring language.")]
    // Re-wrapped: the check flattens whitespace, so a differently broken echo still matches.
    [InlineData("Use plain,\n  reassuring language.\nNever diagnose.")]
    [InlineData("Respond with: summary — the summary itself, 2-4 sentences.")]
    [InlineData("   ")]
    public async Task DiscardsTheSummary_WhenTheModelEchoesItsInstructionsOrSaysNothing(string reply)
    {
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse { Summary = reply });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _digests.DidNotReceive().AddAsync(Arg.Any<DigestEntry>(), Arg.Any<CancellationToken>());
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
        Assert.Contains(
            $"Today so far ({Today}, still in progress — activity totals are partial; "
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

        Assert.Contains(
            "Last night's sleep, 3.6 hours, was well short of the usual 7.0 — a poor night, "
            + "worth saying plainly.",
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
            Assert.Contains("Last night's sleep, 3.6 hours, was well short of the usual 7.0", prompt);
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
        Assert.DoesNotContain("Last night's sleep,", prompt);
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

        Assert.DoesNotContain("Last night's sleep,", prompt);
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

    // ---- Suggestions: three usable ones or none at all ----

    private void ReturnsSuggestions(params string[] suggestions) =>
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A settled night",
                Summary = "A quiet, steady day.",
                Suggestions = suggestions,
            });

    private static bool Suggestions(DigestEntry entry, params string[] expected) =>
        entry.Suggestions is not null && entry.Suggestions.SequenceEqual(expected);

    [Fact]
    public async Task StoresThreeSuggestions_Trimmed()
    {
        // A model that formatted its own list: the bullets and quotes are the model's, not the
        // suggestion's, and they would render as literal characters in the app.
        ReturnsSuggestions(
            "- Ask about the early waking when you call",
            "  \"Suggest a short walk before the light goes\" ",
            "• Sit with them through the afternoon");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => Suggestions(
                d,
                "Ask about the early waking when you call",
                "Suggest a short walk before the light goes",
                "Sit with them through the afternoon")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The section promises three ways to help. Anything short of a full, usable set is dropped so
    /// the apps hide it, rather than rendering a heading over one bullet.
    /// </summary>
    [Fact]
    public async Task StoresNoSuggestions_WhenFewerThanThreeSurvive()
    {
        ReturnsSuggestions(
            "Ask about the early waking when you call", "   ",
            "Respond with: three ways to support them");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestions == null && d.Text == "A quiet, steady day."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoresNoSuggestions_WhenTheModelReturnedNone()
    {
        // The column is nullable precisely so this is representable; an empty list would make the
        // apps decide what an empty section looks like.
        ReturnsSuggestions();

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestions == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DropsTheWholeSet_WhenTheSameSuggestionCameBackTwice()
    {
        // Three ways to help that are the same way twice is worse than no section at all.
        ReturnsSuggestions(
            "Ask about the early waking when you call",
            "ask about the EARLY WAKING when you call",
            "Sit with them through the afternoon");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestions == null), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The failure this prompt change is about: the instructions and the reply schema each carried
    /// three example suggestions, and those three came back word for word for member after member.
    /// The examples are gone; this is the backstop that keeps a return to them off the screen.
    /// </summary>
    [Fact]
    public async Task DropsTheWholeSet_WhenTheSuggestionsAreThePromptsOldExamples()
    {
        ReturnsSuggestions("Ask how they slept", "Suggest a short walk together", "Make their favourite tea");

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestions == null), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The generic phrase is dropped whole, not as a substring — the same words carried on into
    /// something a family could actually act on are what the prompt now asks for.
    /// </summary>
    [Fact]
    public async Task KeepsASuggestionThatCarriesAGenericOpeningIntoSomethingSpecific()
    {
        ReturnsSuggestions(
            "Ask how they slept when you call tonight",
            "Put the heating on before they wake",
            "Sit with them through the afternoon");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => Suggestions(
                d,
                "Ask how they slept when you call tonight",
                "Put the heating on before they wake",
                "Sit with them through the afternoon")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A category of caring is not one of the three; the prompt says so and this holds it.</summary>
    [Fact]
    public async Task DropsTheWholeSet_WhenASuggestionIsABareCategoryOfCaring()
    {
        ReturnsSuggestions("Check in", "Put the heating on before they wake", "Sit with them through the afternoon");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => d.Suggestions == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeepsOnlyTheFirstThree_WhenTheModelOverruns()
    {
        ReturnsSuggestions("One", "Two", "Three", "Four", "Five");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => Suggestions(d, "One", "Two", "Three")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AsksForSuggestionsThatSupportRatherThanTreat()
    {
        string? prompt = null;
        await _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
            Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>());

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.NotNull(prompt);
        Assert.Contains("Never medical advice", prompt);
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
        Assert.Contains("must answer something in the readings above", prompt);
        Assert.Contains("equally true for any person on any day", prompt);
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
    public async Task StoresTheProposedQuestion_WithWhatPromptedIt()
    {
        ReturnsQuestion("Has anything changed at home recently?", "Sleep has been shorter all week.");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _questionnaires.Received(1).AddAsync(Arg.Is<MemberQuestionnaire>(q =>
            q.CardiMemberId == _memberId
            && q.Status == QuestionnaireStatus.Pending
            && q.TriggerContext == "Sleep has been shorter all week."
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
        _questionnaires.HasPendingAsync(_memberId, Arg.Any<CancellationToken>()).Returns(true);

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
                Summary = "You are summarising the readings for a family member.",
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

    // ---- Arrangement helpers for the sections above ----

    private void GivenPreviousSummary(DateTime generatedAt) =>
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                LocalDate = Today,
                GeneratedAtUtc = generatedAt,
            });

    private void GivenAlerts(params Alert[] alerts) =>
        _alerts.GetByCardiMemberAsync(_memberId, Arg.Any<bool>()).Returns(alerts);

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

    private void ReturnsQuestion(string question, string? rationale = null) =>
        _medicalAi.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DigestGenerationService.DigestAiResponse
            {
                Headline = "A settled night",
                Summary = "A settled day: steady heart rate and a good night's sleep.",
                Question = question,
                QuestionRationale = rationale,
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
