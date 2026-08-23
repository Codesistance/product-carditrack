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
/// <see cref="AdviseGenerationService"/> — the batch writer behind the CardiMember Details Tip
/// card's suggestions, now one row per <see cref="AdviseTopic"/> from a single model call. Pins
/// the due-check that keeps this off the status line's aggressive cadence, the grounding contract
/// (an entry with nothing to cite is withheld rather than persisted ungrounded), the per-topic
/// reconciliation (silence removes, a hiccup keeps), and the same not-generated-for guards
/// <see cref="StatusLineGenerationServiceTests"/> pins for its own writer.
/// </summary>
public class AdviseGenerationServiceTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
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
        ModelAnswers(ActivityEntry());
    }

    private static AdviseGenerationService.AdviseEntryAiResponse ActivityEntry(
        string summary = "Steps have been below her usual this week.",
        string suggestion = "A short walk after lunch is worth trying.",
        string? cited = "WHO adult activity guidance") => new()
    {
        Topic = "Activity",
        Summary = summary,
        Suggestion = suggestion,
        GuidelineCited = cited,
    };

    private void ModelAnswers(params AdviseGenerationService.AdviseEntryAiResponse[] entries) =>
        _medicalAi.GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdviseGenerationService.AdviseAiResponse { Entries = entries });

    private static MemberAdvise ExistingRow(
        Guid memberId, AdviseTopic topic = AdviseTopic.Activity, double ageDays = 2) => new()
    {
        CardiMemberId = memberId,
        Topic = topic,
        Summary = "Old summary.",
        Suggestion = "Old suggestion.",
        GuidelineCited = "Old reference",
        GeneratedAtUtc = DateTime.UtcNow.AddDays(-ageDays),
    };

    private AdviseGenerationService CreateSut() =>
        new(_unitOfWork, _medicalAi, PromptContextFactory.Composer(_unitOfWork),
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
            && a.GuidelineCited == "WHO adult activity guidance"));
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>One call, several rows — the whole point of topic scoping without multiplying
    /// MedGemma cost.</summary>
    [Fact]
    public async Task SeveralTopics_AllPersistFromTheOneCall()
    {
        ModelAnswers(
            ActivityEntry(),
            new AdviseGenerationService.AdviseEntryAiResponse
            {
                Topic = "Sleep",
                Summary = "Her nights have been shorter than her usual.",
                Suggestion = "A steadier bedtime could help her settle.",
                GuidelineCited = "NSF sleep duration guidance",
            });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.Received(1).AddAsync(Arg.Is<MemberAdvise>(a => a.Topic == AdviseTopic.Activity));
        await _advises.Received(1).AddAsync(Arg.Is<MemberAdvise>(a => a.Topic == AdviseTopic.Sleep));
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>An out-of-vocabulary topic is dropped like any other unrecognised model answer —
    /// never coerced, never persisted.</summary>
    [Fact]
    public async Task AnUnrecognisedTopic_IsDropped()
    {
        ModelAnswers(new AdviseGenerationService.AdviseEntryAiResponse
        {
            Topic = "Nutrition",
            Summary = "Meals look irregular.",
            Suggestion = "A regular lunch could help.",
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

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>A topic the model stayed silent on has its row withdrawn — the prompt makes
    /// silence deliberate, and a suggestion the readings no longer support is worse than none.</summary>
    [Fact]
    public async Task ATopicTheModelStaysSilentOn_HasItsRowRemoved()
    {
        var sleepRow = ExistingRow(_memberId, AdviseTopic.Sleep);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[sleepRow]);
        ModelAnswers(ActivityEntry());

        await CreateSut().RegenerateIfDueAsync(_memberId);

        _advises.Received(1).Remove(sleepRow);
        await _advises.Received(1).AddAsync(Arg.Is<MemberAdvise>(a => a.Topic == AdviseTopic.Activity));
    }

    // A blank summary or suggestion reads as a transient model hiccup — the previous suggestion
    // for that topic beats none, so its row is kept rather than treated as deliberate silence.
    [Theory]
    [InlineData("", "A short walk after lunch is worth trying.")]
    [InlineData("Steps have been below her usual this week.", "   ")]
    public async Task ABlankEntry_LeavesTheExistingTopicRowUntouched(string summary, string suggestion)
    {
        var existing = ExistingRow(_memberId);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[existing]);
        ModelAnswers(ActivityEntry(summary, suggestion));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("Old suggestion.", existing.Suggestion);
        _advises.DidNotReceive().Remove(Arg.Any<MemberAdvise>());
        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// Unlike a blank field, an entry citing nothing is the model declining to ground the
    /// suggestion, and an empty entries list is it saying nothing anywhere fits — both deliberate.
    /// The honest response is withdrawal, not serving a suggestion that may no longer apply.
    /// </summary>
    [Fact]
    public async Task NothingToCite_RemovesTheExistingTopicRowRatherThanKeepingIt()
    {
        var existing = ExistingRow(_memberId);
        _advises.GetAllByCardiMemberAsync(_memberId).Returns((IReadOnlyList<MemberAdvise>)[existing]);
        ModelAnswers(ActivityEntry(cited: null));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        _advises.Received(1).Remove(existing);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task NothingAnywhere_WithNoExistingRows_WritesNothing()
    {
        ModelAnswers();

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

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
            _ => throw new DbUpdateException("duplicate key value violates unique constraint"),
            _ => 1);

        await CreateSut().RegenerateIfDueAsync(_memberId);

        _advises.Received(1).Remove(Arg.Is<MemberAdvise>(a => a.CardiMemberId == _memberId));
        Assert.Equal("A short walk after lunch is worth trying.", winner.Suggestion);
        await _unitOfWork.Received(2).SaveChangesAsync();
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

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
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

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
    }

    [Fact]
    public async Task Prompt_GroundsInTheHealthReference_NotClinicalLanguage()
    {
        await CreateSut().RegenerateIfDueAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("Write as a caregiver would", prompt);
        Assert.Contains("--- General health reference ---", prompt);
        Assert.Contains("WHO, 2020", prompt);
        Assert.Contains("never a diagnosis, a prescription, or a change to medication or treatment", prompt);
        Assert.Contains("worth mentioning to their doctor", prompt);
        Assert.DoesNotContain("medical AI assistant", prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A reply that crosses the one boundary this generation lives inside is withheld, not
    /// persisted — per entry, exactly as it was for the single row.
    /// </summary>
    [Theory]
    [InlineData("His readings suggest a heart condition.", "A short walk after lunch is worth trying.")]
    [InlineData("Steps have been below her usual.", "She should stop taking the evening dose.")]
    [InlineData("Steps have been below her usual.", "Ask the GP for a prescription to help her sleep.")]
    [InlineData("This looks like sleep apnoea has been diagnosed.", "A steadier bedtime is worth trying.")]
    public async Task AnEntryNamingAConditionOrATreatment_IsWithheld(string summary, string suggestion)
    {
        ModelAnswers(ActivityEntry(summary, suggestion));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
    }

    /// <summary>
    /// An everyday suggestion is not discarded for sounding vaguely health-adjacent — a false
    /// discard costs the caregiver that day's suggestion entirely.
    /// </summary>
    [Theory]
    [InlineData("Warm conditions this week.", "A short walk after lunch is worth trying.")]
    [InlineData("Her sleep has been shorter than usual.", "A steadier bedtime could help her settle.")]
    [InlineData("Steps are down.", "Getting outside for 15 minutes a day is worth a try.")]
    public async Task AnEverydaySuggestion_IsNotDiscarded(string summary, string suggestion)
    {
        ModelAnswers(ActivityEntry(summary, suggestion));

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
        ModelAnswers(ActivityEntry(cited: cited));

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
        ModelAnswers(ActivityEntry(cited: "WHO adult activity, none of the sleep references"));

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.Received(1).AddAsync(Arg.Any<MemberAdvise>());
    }

    /// <summary>
    /// The prompt never says "wellness" — MedGemma completes from the nearest text, and the word
    /// put "it's a general wellness thing" in front of a caregiver once already.
    /// </summary>
    [Fact]
    public async Task Prompt_NeverSaysWellness()
    {
        await CreateSut().RegenerateIfDueAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.DoesNotContain("wellness", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
