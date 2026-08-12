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
    private readonly IDigestRepository _digests = Substitute.For<IDigestRepository>();
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
        _unitOfWork.Digests.Returns(_digests);

        // Defaults: one active London-anchored member whose data landed half an hour ago, and who
        // has never had a summary written.
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());
        SetupAnchorTimeZone("Europe/London");
        SetupActivity(DataLandedAt);
        _digests.GetLatestAsync(_memberId, DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns((DigestEntry?)null);
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
        new(_unitOfWork, _medicalAi, NullLogger<DigestGenerationService>.Instance);

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
        Assert.Contains($"{Today}: steps=5000", prompt);
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
        ReturnsSuggestions("- Ask how they slept", "  \"Suggest a short walk\" ", "• Sit with them a while");

        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        await _digests.Received(1).AddAsync(
            Arg.Is<DigestEntry>(d => Suggestions(
                d, "Ask how they slept", "Suggest a short walk", "Sit with them a while")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The section promises three ways to help. Anything short of a full, usable set is dropped so
    /// the apps hide it, rather than rendering a heading over one bullet.
    /// </summary>
    [Fact]
    public async Task StoresNoSuggestions_WhenFewerThanThreeSurvive()
    {
        ReturnsSuggestions("Ask how they slept", "   ", "Respond with: three ways to support them");

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
        ReturnsSuggestions("Ask how they slept", "ask how they SLEPT", "Sit with them a while");

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
}
