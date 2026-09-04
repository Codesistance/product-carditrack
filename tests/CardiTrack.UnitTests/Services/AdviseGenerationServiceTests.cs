using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="AdviseGenerationService"/> — the two-slot batch writer behind the CardiMember
/// Details "Something to try" card: MedGemma's clinical read of where the readings fall short,
/// rewritten for the family by the Rewrite slot, one row per <see cref="AdviseTopic"/>. Pins the
/// due-check (age and <see cref="MemberAdvise.PromptVersion"/> alike), the grounding contract (an
/// entry with nothing to cite is withheld rather than persisted ungrounded), the per-topic
/// reconciliation (silence removes, a hiccup keeps — and a failed rewrite is a hiccup), the copy
/// guards on the rewritten text, and the same not-generated-for guards
/// <see cref="StatusLineGenerationServiceTests"/> pins for its own writer.
/// </summary>
public class AdviseGenerationServiceTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IMemberAdviseRepository _advises = Substitute.For<IMemberAdviseRepository>();

    private readonly Guid _memberId = Guid.NewGuid();

    public AdviseGenerationServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.MemberAdvises.Returns(_advises);

        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
        });
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[]);
        ClinicalAnswers(ActivityFinding());
        RewriteAnswers(ActivityCopy());
    }

    private static AdviseGenerationService.AdviseClinicalEntryAiResponse ActivityFinding(
        string finding = "Steps sit below the member's 30-day usual.",
        string action = "A short daily walk would close the gap.",
        string? cited = "WHO adult activity guidance") => new()
    {
        Topic = "Activity",
        Finding = finding,
        Action = action,
        GuidelineCited = cited,
    };

    private static AdviseGenerationService.AdviseRewriteEntryAiResponse ActivityCopy(
        string summary = "Steps have been below her usual this week.",
        string suggestion = "A short walk after lunch is worth trying.") => new()
    {
        Topic = "Activity",
        Summary = summary,
        Suggestion = suggestion,
    };

    private void ClinicalAnswers(params AdviseGenerationService.AdviseClinicalEntryAiResponse[] entries) =>
        _medicalAi.GenerateStructuredAsync<AdviseGenerationService.AdviseClinicalAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdviseGenerationService.AdviseClinicalAiResponse { Entries = entries });

    private void RewriteAnswers(params AdviseGenerationService.AdviseRewriteEntryAiResponse[] entries) =>
        _rewriteAi.GenerateStructuredAsync<AdviseGenerationService.AdviseRewriteAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdviseGenerationService.AdviseRewriteAiResponse { Entries = entries });

    private static MemberAdvise ExistingRow(
        Guid memberId,
        AdviseTopic topic = AdviseTopic.Activity,
        double ageDays = 2,
        int promptVersion = AdviseGenerationService.CurrentPromptVersion) => new()
    {
        CardiMemberId = memberId,
        Topic = topic,
        Summary = "Old summary.",
        Suggestion = "Old suggestion.",
        GuidelineCited = "Old reference",
        GeneratedAtUtc = DateTime.UtcNow.AddDays(-ageDays),
        PromptVersion = promptVersion,
    };

    /// <summary>The shape the narrowed catch filters for: a DbUpdateException whose inner is
    /// Postgres's unique violation — any other write failure now bubbles.</summary>
    private static DbUpdateException UniqueViolation() => new(
        "duplicate key value violates unique constraint",
        new Npgsql.PostgresException(
            "duplicate key value violates unique constraint", "ERROR", "ERROR",
            Npgsql.PostgresErrorCodes.UniqueViolation));

    private AdviseGenerationService CreateSut() =>
        new(_unitOfWork, _medicalAi, _rewriteAi, PromptContextFactory.Composer(_unitOfWork),
            NullLogger<AdviseGenerationService>.Instance);

    [Fact]
    public async Task NoExistingRows_PersistsAFreshTopicRow()
    {
        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.Received(1).AddAsync(Arg.Is<MemberAdvise>(a =>
            a.CardiMemberId == _memberId
            && a.Topic == AdviseTopic.Activity
            && a.Summary == "Steps have been below her usual this week."
            && a.Suggestion == "A short walk after lunch is worth trying."
            && a.GuidelineCited == "WHO adult activity guidance"
            && a.PromptVersion == AdviseGenerationService.CurrentPromptVersion));
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>One clinical call and one rewrite call, several rows — the whole point of topic
    /// scoping without multiplying model cost.</summary>
    [Fact]
    public async Task SeveralTopics_AllPersistFromTheOneCallPair()
    {
        ClinicalAnswers(
            ActivityFinding(),
            new AdviseGenerationService.AdviseClinicalEntryAiResponse
            {
                Topic = "Sleep",
                Finding = "Nights run under the 7-hour reference.",
                Action = "A steadier bedtime would lengthen them.",
                GuidelineCited = "NSF sleep duration guidance",
            });
        RewriteAnswers(
            ActivityCopy(),
            new AdviseGenerationService.AdviseRewriteEntryAiResponse
            {
                Topic = "Sleep",
                Summary = "Her nights have been shorter than her usual.",
                Suggestion = "A steadier bedtime could help her settle.",
            });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.Received(1).AddAsync(Arg.Is<MemberAdvise>(a => a.Topic == AdviseTopic.Activity));
        await _advises.Received(1).AddAsync(Arg.Is<MemberAdvise>(a => a.Topic == AdviseTopic.Sleep));
        await _unitOfWork.Received(1).SaveChangesAsync();
        await _rewriteAi.Received(1).GenerateStructuredAsync<AdviseGenerationService.AdviseRewriteAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>An out-of-vocabulary topic is dropped like any other unrecognised model answer —
    /// never coerced, never persisted.</summary>
    [Fact]
    public async Task AnUnrecognisedTopic_IsDropped()
    {
        ClinicalAnswers(new AdviseGenerationService.AdviseClinicalEntryAiResponse
        {
            Topic = "Nutrition",
            Finding = "Meals look irregular.",
            Action = "A regular lunch could help.",
            GuidelineCited = "WHO adult activity guidance",
        });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
    }

    [Fact]
    public async Task ExistingRowPastTheInterval_IsOverwrittenInPlace()
    {
        var existing = ExistingRow(_memberId);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[existing]);

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("A short walk after lunch is worth trying.", existing.Suggestion);
        Assert.Equal(AdviseGenerationService.CurrentPromptVersion, existing.PromptVersion);
        Assert.True(existing.GeneratedAtUtc > DateTime.UtcNow.AddMinutes(-1));
        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>The whole cost discipline: rows regenerated recently are left alone rather than
    /// spending another model call — the newest row's age gates the pass, since the batch writes
    /// every topic together.</summary>
    [Fact]
    public async Task ExistingRowWithinTheInterval_IsNotRegenerated()
    {
        _advises.GetAllByCardiMemberAsync(_memberId).Returns(
            (IReadOnlyList<MemberAdvise>)[ExistingRow(_memberId, ageDays: 0.05)]);

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<AdviseGenerationService.AdviseClinicalAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// The version gate: a fresh row written by an older brief is due now, whatever its age —
    /// this is what makes a deployed prompt change visible within one digest pass instead of
    /// hiding behind the daily interval for up to the serve window.
    /// </summary>
    [Fact]
    public async Task AFreshRowFromAnOlderPromptVersion_IsRegeneratedAnyway()
    {
        var stale = ExistingRow(_memberId, ageDays: 0.05, promptVersion: 0);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[stale]);

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("A short walk after lunch is worth trying.", stale.Suggestion);
        Assert.Equal(AdviseGenerationService.CurrentPromptVersion, stale.PromptVersion);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>A topic the clinical read stays silent on has its row withdrawn — the brief makes
    /// silence deliberate, and a suggestion the readings no longer support is worse than none.</summary>
    [Fact]
    public async Task ATopicTheModelStaysSilentOn_HasItsRowRemoved()
    {
        var sleepRow = ExistingRow(_memberId, AdviseTopic.Sleep);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[sleepRow]);
        ClinicalAnswers(ActivityFinding());

        await CreateSut().RegenerateIfDueAsync(_memberId);

        _advises.Received(1).Remove(sleepRow);
        await _advises.Received(1).AddAsync(Arg.Is<MemberAdvise>(a => a.Topic == AdviseTopic.Activity));
    }

    // A blank clinical finding or action reads as a transient model hiccup — the previous
    // suggestion for that topic beats none, so its row is kept rather than treated as deliberate
    // silence.
    [Theory]
    [InlineData("", "A short daily walk would close the gap.")]
    [InlineData("Steps sit below the member's 30-day usual.", "   ")]
    public async Task ABlankClinicalEntry_LeavesTheExistingTopicRowUntouched(string finding, string action)
    {
        var existing = ExistingRow(_memberId);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[existing]);
        ClinicalAnswers(ActivityFinding(finding, action));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("Old suggestion.", existing.Suggestion);
        _advises.DidNotReceive().Remove(Arg.Any<MemberAdvise>());
        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// Unlike a blank field, an entry citing nothing is the model declining to ground the
    /// suggestion, and an empty entries list is it saying nothing anywhere falls short — both
    /// deliberate. The honest response is withdrawal, not serving a suggestion that may no longer
    /// apply.
    /// </summary>
    [Fact]
    public async Task NothingToCite_RemovesTheExistingTopicRowRatherThanKeepingIt()
    {
        var existing = ExistingRow(_memberId);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[existing]);
        ClinicalAnswers(ActivityFinding(cited: null));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        _advises.Received(1).Remove(existing);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task NothingAnywhere_WithNoExistingRows_WritesNothing()
    {
        ClinicalAnswers();

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
        // No clinical survivors means no rewrite call either — the cheap half still isn't free.
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<AdviseGenerationService.AdviseRewriteAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- The rewrite slot ----

    /// <summary>
    /// The addressing contract: the rewrite names the member through the placeholder, and code —
    /// never a model — resolves it to the real first name.
    /// </summary>
    [Fact]
    public async Task TheNameToken_IsResolvedInCode()
    {
        RewriteAnswers(ActivityCopy(
            summary: "CardiTrackCardiMember's steps have been below her usual this week.",
            suggestion: "The family could join CardiTrackCardiMember for a short walk after lunch."));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.Received(1).AddAsync(Arg.Is<MemberAdvise>(a =>
            a.Summary == "Margaret's steps have been below her usual this week."
            && a.Suggestion == "The family could join Margaret for a short walk after lunch."));
    }

    /// <summary>
    /// A member with no name on file cannot have the token resolved, and a leftover token must
    /// never reach a caregiver — the entry is a hiccup, keeping whatever row already serves.
    /// </summary>
    [Fact]
    public async Task ATokenWithNoNameOnFile_KeepsThePreviousRow()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "",
            IsActive = true,
        });
        var existing = ExistingRow(_memberId, promptVersion: 0);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[existing]);
        RewriteAnswers(ActivityCopy(
            suggestion: "The family could join CardiTrackCardiMember for a short walk."));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("Old suggestion.", existing.Suggestion);
        _advises.DidNotReceive().Remove(Arg.Any<MemberAdvise>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// A failed rewrite call keeps every row and removes nothing: the clinical read was sound,
    /// and losing the copy step must not read as the readings having gone quiet. The version gate
    /// retries the pair next pass.
    /// </summary>
    [Fact]
    public async Task ARewriteFailure_KeepsEveryExistingRow()
    {
        var existing = ExistingRow(_memberId, promptVersion: 0);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[existing]);
        _rewriteAi.GenerateStructuredAsync<AdviseGenerationService.AdviseRewriteAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<AdviseGenerationService.AdviseRewriteAiResponse>>(
                _ => throw new HttpRequestException("rewrite slot unavailable"));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("Old suggestion.", existing.Suggestion);
        _advises.DidNotReceive().Remove(Arg.Any<MemberAdvise>());
        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// A note the rewrite skips is a copy hiccup, not clinical silence — the previous row for
    /// that topic stays.
    /// </summary>
    [Fact]
    public async Task ANoteTheRewriteSkips_KeepsThePreviousRow()
    {
        var existing = ExistingRow(_memberId, promptVersion: 0);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[existing]);
        RewriteAnswers();

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("Old suggestion.", existing.Suggestion);
        _advises.DidNotReceive().Remove(Arg.Any<MemberAdvise>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// Copy that is the brief restating itself is rejected — "It's just a suggestion, worth
    /// mentioning to their doctor" reached a caregiver verbatim before this guard existed, and
    /// the doctor line is fixed UI copy on the card now.
    /// </summary>
    [Theory]
    [InlineData("Steps have been below her usual.", "It's just a suggestion, worth mentioning to their doctor.")]
    [InlineData("Steps have been below her usual.", "A walk, grounded in the reference below, could help.")]
    public async Task CopyEchoingTheBrief_KeepsThePreviousRow(string summary, string suggestion)
    {
        var existing = ExistingRow(_memberId, promptVersion: 0);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[existing]);
        RewriteAnswers(ActivityCopy(summary, suggestion));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("Old suggestion.", existing.Suggestion);
        _advises.DidNotReceive().Remove(Arg.Any<MemberAdvise>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// A summary quoting figures is a lab note — "going from 3949 steps to 9099 steps" shipped
    /// once — and the figures already live on the trend cards. The suggestion may still carry a
    /// number ("15-20 minutes"); only the summary is held to this.
    /// </summary>
    [Fact]
    public async Task ASummaryQuotingFigures_KeepsThePreviousRow()
    {
        var existing = ExistingRow(_memberId, promptVersion: 0);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[existing]);
        RewriteAnswers(ActivityCopy(summary: "Steps went from 3949 to 9099 this week."));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("Old suggestion.", existing.Suggestion);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task ASuggestionCarryingMinutes_IsNotDiscarded()
    {
        RewriteAnswers(ActivityCopy(
            suggestion: "A 15-20 minute walk together after lunch is worth trying."));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.Received(1).AddAsync(Arg.Any<MemberAdvise>());
    }

    // ---- Races and failures ----

    /// <summary>
    /// The digest pass and any future second caller can regenerate the same member concurrently;
    /// both read no-rows, both insert, and the unique (member, topic) index fails the loser. Same
    /// recovery as the single-row version: detach the staged inserts and write over the winner's.
    /// </summary>
    [Fact]
    public async Task LosingTheInsertRace_WritesOverTheWinnersRow()
    {
        var winner = new MemberAdvise
        {
            CardiMemberId = _memberId,
            Topic = AdviseTopic.Activity,
            Summary = "Winner summary.",
            Suggestion = "Winner suggestion.",
            GuidelineCited = "Winner reference",
            GeneratedAtUtc = DateTime.UtcNow,
        };
        _advises.GetAllByCardiMemberAsync(_memberId).Returns(
            (IReadOnlyList<MemberAdvise>)[], (IReadOnlyList<MemberAdvise>)[winner]);
        _unitOfWork.SaveChangesAsync().Returns(
            _ => throw UniqueViolation(),
            _ => 1);

        await CreateSut().RegenerateIfDueAsync(_memberId);

        _advises.Received(1).Remove(Arg.Is<MemberAdvise>(a => a.CardiMemberId == _memberId));
        Assert.Equal("A short walk after lunch is worth trying.", winner.Suggestion);
        Assert.Equal(AdviseGenerationService.CurrentPromptVersion, winner.PromptVersion);
        await _unitOfWork.Received(2).SaveChangesAsync();
    }

    /// <summary>
    /// The race can be on a different topic's index than the ones this pass would add: a
    /// concurrent pass wrote Activity only, this pass wants Activity and Sleep, and the aborted
    /// batch took the Sleep insert down with it. The recovery must re-stage the topics that have
    /// no winner — the first version only overwrote winners and silently dropped the rest.
    /// </summary>
    [Fact]
    public async Task LosingTheRace_StillWritesTheTopicsTheWinnerDidNotHave()
    {
        ClinicalAnswers(
            ActivityFinding(),
            new AdviseGenerationService.AdviseClinicalEntryAiResponse
            {
                Topic = "Sleep",
                Finding = "Nights run under the 7-hour reference.",
                Action = "A steadier bedtime would lengthen them.",
                GuidelineCited = "NSF sleep duration guidance",
            });
        RewriteAnswers(
            ActivityCopy(),
            new AdviseGenerationService.AdviseRewriteEntryAiResponse
            {
                Topic = "Sleep",
                Summary = "Her nights have been shorter than her usual.",
                Suggestion = "A steadier bedtime could help her settle.",
            });
        var winner = new MemberAdvise
        {
            CardiMemberId = _memberId,
            Topic = AdviseTopic.Activity,
            Summary = "Winner summary.",
            Suggestion = "Winner suggestion.",
            GuidelineCited = "Winner reference",
            GeneratedAtUtc = DateTime.UtcNow,
        };
        _advises.GetAllByCardiMemberAsync(_memberId).Returns(
            (IReadOnlyList<MemberAdvise>)[], (IReadOnlyList<MemberAdvise>)[winner]);
        _unitOfWork.SaveChangesAsync().Returns(
            _ => throw UniqueViolation(),
            _ => 1);

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("A short walk after lunch is worth trying.", winner.Suggestion);
        // Sleep staged twice: once in the aborted batch, once re-staged by the recovery.
        await _advises.Received(2).AddAsync(Arg.Is<MemberAdvise>(a => a.Topic == AdviseTopic.Sleep));
        await _unitOfWork.Received(2).SaveChangesAsync();
    }

    /// <summary>The recovery is for the insert race only: any other write failure bubbles rather
    /// than being "recovered" into a second save that hides the real problem.</summary>
    [Fact]
    public async Task ANonRaceWriteFailure_Bubbles()
    {
        _unitOfWork.SaveChangesAsync().Returns<Task<int>>(
            _ => throw new DbUpdateException("value too long for type character varying(500)"));

        await Assert.ThrowsAsync<DbUpdateException>(() => CreateSut().RegenerateIfDueAsync(_memberId));
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task PausedMember_IsNeverGeneratedFor()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = true,
            MonitoringPausedUntil = DateTime.UtcNow.AddHours(4),
        });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<AdviseGenerationService.AdviseClinicalAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
    }

    [Fact]
    public async Task InactiveMember_IsNeverGeneratedFor()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = false,
        });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<AdviseGenerationService.AdviseClinicalAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
    }

    // ---- The two briefs ----

    /// <summary>
    /// The clinical brief is data only: it grounds in the health reference and carries the
    /// treatment ban, and it does not carry the caregiver voice — that is the rewrite's, per the
    /// two-slot contract member chat set.
    /// </summary>
    [Fact]
    public async Task TheClinicalPrompt_GroundsInTheHealthReference_AndCarriesNoCaregiverVoice()
    {
        await CreateSut().RegenerateIfDueAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("--- General health reference ---", prompt);
        Assert.Contains("WHO, 2020", prompt);
        Assert.Contains("never a diagnosis, a prescription, or a change to medication or treatment", prompt);
        Assert.Contains("never a reason to suggest more of the same", prompt);
        Assert.DoesNotContain("Write as a caregiver would", prompt);
        Assert.DoesNotContain("medical AI assistant", prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The rewrite brief is the voice and the addressing: the caregiver register, the family
    /// written to about the member, the member named only through the placeholder — and only the
    /// clinical notes reach it, never the member context or readings (DPIA row A20).
    /// </summary>
    [Fact]
    public async Task TheRewritePrompt_CarriesTheVoiceAndOnlyTheNotes()
    {
        await CreateSut().RegenerateIfDueAsync(_memberId);

        var prompt = (string)_rewriteAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("Write as a caregiver would", prompt);
        // The doctor line is fixed UI copy and a phrase the copy guard rejects — a brief carrying
        // it would instruct the model into its own guard.
        Assert.DoesNotContain("worth mentioning to their doctor", prompt);
        Assert.Contains("CardiTrackCardiMember", prompt);
        Assert.Contains("never quote a figure", prompt);
        Assert.Contains("--- Clinical notes to rewrite ---", prompt);
        Assert.Contains("Steps sit below the member's 30-day usual.", prompt);
        Assert.DoesNotContain("--- General health reference ---", prompt);
        Assert.DoesNotContain("--- Baseline ---", prompt);
        Assert.DoesNotContain("Margaret", prompt);
        Assert.DoesNotContain("Age:", prompt);
    }

    /// <summary>
    /// A clinical entry proposing a treatment is withheld before the rewrite ever sees it. That
    /// boundary is about scope, not register: Advise suggests an everyday action, so a note about
    /// a dose is the wrong note whatever a rewrite would do with it.
    /// </summary>
    [Theory]
    [InlineData("Steps sit below the usual.", "Stop taking the evening dose.")]
    [InlineData("Steps sit below the usual.", "A prescription to help sleep is warranted.")]
    public async Task AClinicalEntryProposingATreatment_IsWithheld(string finding, string action)
    {
        ClinicalAnswers(ActivityFinding(finding, action));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<AdviseGenerationService.AdviseRewriteAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A clinical entry naming a condition now reaches the rewrite instead of being discarded.
    /// This is the point of the split: MedGemma is medically tuned, its note is read by the
    /// rewrite model and by nobody else, and the register boundary is held one stage later — on
    /// the copy a caregiver reads, where <see cref="AdviseRegisterGuards.ReadsAsClinical"/> still
    /// runs. Discarding it here was the throttle.
    /// </summary>
    [Theory]
    [InlineData("The readings suggest a heart condition.", "A short daily walk would close the gap.")]
    [InlineData("This looks like sleep apnoea has been diagnosed.", "A steadier bedtime would help.")]
    public async Task AClinicalEntryNamingACondition_ReachesTheRewrite(string finding, string action)
    {
        ClinicalAnswers(ActivityFinding(finding, action));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        var prompt = (string)_rewriteAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains(finding, prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the boundary still holds where it matters: rewritten copy that carries the condition
    /// name through is withheld, so unclamping the clinical read did not move the line a caregiver
    /// sits behind — it moved which stage enforces it.
    /// </summary>
    [Fact]
    public async Task RewrittenCopyNamingACondition_IsStillWithheld()
    {
        ClinicalAnswers(ActivityFinding(
            "The readings suggest a heart condition.", "A short daily walk would close the gap."));
        RewriteAnswers(ActivityCopy(
            "There may be a heart condition behind this.", "A short walk together after lunch."));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
    }

    /// <summary>
    /// An everyday finding is not discarded for sounding vaguely health-adjacent — a false
    /// discard costs the caregiver that day's suggestion entirely.
    /// </summary>
    [Theory]
    [InlineData("Warm conditions this week beside a quieter day.", "A short walk in the cooler evening.")]
    [InlineData("Sleep runs shorter than the member's usual.", "A steadier bedtime.")]
    [InlineData("Steps sit under the weekly reference.", "Fifteen minutes outside each day.")]
    public async Task AnEverydayClinicalEntry_IsNotDiscarded(string finding, string action)
    {
        ClinicalAnswers(ActivityFinding(finding, action));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.Received(1).AddAsync(Arg.Any<MemberAdvise>());
    }

    /// <summary>
    /// The traceability check the field exists for is that a reference was named, not that the
    /// field was filled — "N/A" passed a nonblank check and made an ungrounded suggestion look
    /// grounded to every reader of the row.
    /// </summary>
    [Theory]
    [InlineData("N/A")]
    [InlineData("n/a")]
    [InlineData("None")]
    [InlineData("none.")]
    [InlineData("Not applicable")]
    [InlineData("unknown")]
    [InlineData("-")]
    public async Task ACitationNamingNoReference_WithholdsTheEntry(string cited)
    {
        ClinicalAnswers(ActivityFinding(cited: cited));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
    }

    /// <summary>
    /// "None" inside a sentence about the references is an answer about them, not a refusal to
    /// name one — the placeholders are matched whole for this reason.
    /// </summary>
    [Fact]
    public async Task ACitationMentioningNoneInASentence_IsStillACitation()
    {
        ClinicalAnswers(ActivityFinding(cited: "WHO adult activity, none of the sleep references"));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.Received(1).AddAsync(Arg.Any<MemberAdvise>());
    }

    /// <summary>
    /// Neither brief says "wellness" — MedGemma completes from the nearest text, and the word put
    /// "it's a general wellness thing" in front of a caregiver once already.
    /// </summary>
    [Fact]
    public async Task NeitherPrompt_EverSaysWellness()
    {
        await CreateSut().RegenerateIfDueAsync(_memberId);

        var clinical = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        var rewrite = (string)_rewriteAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.DoesNotContain("wellness", clinical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wellness", rewrite, StringComparison.OrdinalIgnoreCase);
    }
}
