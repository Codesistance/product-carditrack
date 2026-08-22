using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="AdviseGenerationService"/> — the batch writer behind the CardiMember Details Tip
/// card's wellness suggestion. Pins the due-check that keeps this off the status line's aggressive
/// cadence, the grounding contract (a reply with nothing to cite withholds the row rather than
/// persisting an ungrounded suggestion), and the same not-generated-for guards
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
        _advises.GetByCardiMemberAsync(_memberId).Returns((MemberAdvise?)null);
        _medicalAi.GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdviseGenerationService.AdviseAiResponse
            {
                Summary = "Steps have been below her usual this week.",
                Suggestion = "A short walk after lunch is worth trying.",
                GuidelineCited = "WHO adult activity guidance",
            });
    }

    private AdviseGenerationService CreateSut() =>
        new(_unitOfWork, _medicalAi, PromptContextFactory.Composer(_unitOfWork),
            NullLogger<AdviseGenerationService>.Instance);

    [Fact]
    public async Task NoExistingRow_PersistsAFreshOne()
    {
        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.Received(1).AddAsync(Arg.Is<MemberAdvise>(a =>
            a.CardiMemberId == _memberId
            && a.Summary == "Steps have been below her usual this week."
            && a.Suggestion == "A short walk after lunch is worth trying."
            && a.GuidelineCited == "WHO adult activity guidance"));
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ExistingRowPastTheInterval_IsOverwrittenInPlace()
    {
        var existing = new MemberAdvise
        {
            CardiMemberId = _memberId,
            Summary = "Old summary.",
            Suggestion = "Old suggestion.",
            GuidelineCited = "Old reference",
            GeneratedAtUtc = DateTime.UtcNow.AddDays(-2),
        };
        _advises.GetByCardiMemberAsync(_memberId).Returns(existing);

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("A short walk after lunch is worth trying.", existing.Suggestion);
        Assert.True(existing.GeneratedAtUtc > DateTime.UtcNow.AddMinutes(-1));
        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>The whole cost discipline: a row regenerated recently is left alone rather than
    /// spending another model call, regardless of how many times a digest pass calls this.</summary>
    [Fact]
    public async Task ExistingRowWithinTheInterval_IsNotRegenerated()
    {
        _advises.GetByCardiMemberAsync(_memberId).Returns(new MemberAdvise
        {
            CardiMemberId = _memberId,
            Summary = "Recent summary.",
            Suggestion = "Recent suggestion.",
            GuidelineCited = "Recent reference",
            GeneratedAtUtc = DateTime.UtcNow.AddHours(-1),
        });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    // A blank summary or suggestion reads as a transient model hiccup — the previous suggestion
    // beats no suggestion, so nothing is written.
    [Theory]
    [InlineData("", "A short walk after lunch is worth trying.")]
    [InlineData("Steps have been below her usual this week.", "   ")]
    public async Task BlankSummaryOrSuggestion_LeavesTheExistingRowUntouched(string summary, string suggestion)
    {
        var existing = new MemberAdvise
        {
            CardiMemberId = _memberId,
            Summary = "Old summary.",
            Suggestion = "Old suggestion.",
            GuidelineCited = "Old reference",
            GeneratedAtUtc = DateTime.UtcNow.AddDays(-2),
        };
        _advises.GetByCardiMemberAsync(_memberId).Returns(existing);
        _medicalAi.GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdviseGenerationService.AdviseAiResponse
            {
                Summary = summary,
                Suggestion = suggestion,
                GuidelineCited = "WHO adult activity guidance",
            });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        Assert.Equal("Old suggestion.", existing.Suggestion);
        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// Unlike a blank field, an empty <c>guidelineCited</c> beside a well-formed summary and
    /// suggestion is the model doing exactly what it was asked: saying there is nothing to suggest.
    /// The honest response is to withhold the row, not to keep serving a suggestion that may no
    /// longer apply.
    /// </summary>
    [Fact]
    public async Task NothingToCite_RemovesAnExistingRowRatherThanKeepingIt()
    {
        var existing = new MemberAdvise
        {
            CardiMemberId = _memberId,
            Summary = "Old summary.",
            Suggestion = "Old suggestion.",
            GuidelineCited = "Old reference",
            GeneratedAtUtc = DateTime.UtcNow.AddDays(-2),
        };
        _advises.GetByCardiMemberAsync(_memberId).Returns(existing);
        _medicalAi.GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdviseGenerationService.AdviseAiResponse
            {
                Summary = "Everything looks in line with her usual pattern.",
                Suggestion = "There is nothing to suggest right now.",
                GuidelineCited = null,
            });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        _advises.Received(1).Remove(existing);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task NothingToCite_WithNoExistingRow_WritesNothing()
    {
        _medicalAi.GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdviseGenerationService.AdviseAiResponse
            {
                Summary = "Everything looks in line with her usual pattern.",
                Suggestion = "There is nothing to suggest right now.",
                GuidelineCited = null,
            });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// The digest pass and any future second caller can regenerate the same member concurrently;
    /// both read no-row, both insert, and the unique index fails the loser. Same recovery as
    /// StatusLineGenerationService: detach the staged insert and write over the winner's row.
    /// </summary>
    [Fact]
    public async Task LosingTheInsertRace_WritesOverTheWinnersRow()
    {
        var winner = new MemberAdvise
        {
            CardiMemberId = _memberId,
            Summary = "Winner summary.",
            Suggestion = "Winner suggestion.",
            GuidelineCited = "Winner reference",
            GeneratedAtUtc = DateTime.UtcNow,
        };
        _advises.GetByCardiMemberAsync(_memberId).Returns((MemberAdvise?)null, winner);
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
    /// persisted. The prompt states the boundary and cannot enforce it — the line
    /// <c>JournalNoCondition</c>'s remark draws in one sentence — and Advise was the only
    /// comparable generation on the platform with no check behind the asking.
    /// </summary>
    [Theory]
    [InlineData("His readings suggest a heart condition.", "A short walk after lunch is worth trying.")]
    [InlineData("Steps have been below her usual.", "She should stop taking the evening dose.")]
    [InlineData("Steps have been below her usual.", "Ask the GP for a prescription to help her sleep.")]
    [InlineData("This looks like sleep apnoea has been diagnosed.", "A steadier bedtime is worth trying.")]
    public async Task AReplyNamingAConditionOrATreatment_IsWithheld(string summary, string suggestion)
    {
        _medicalAi.GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdviseGenerationService.AdviseAiResponse
            {
                Summary = summary,
                Suggestion = suggestion,
                GuidelineCited = "WHO adult activity guidance",
            });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
    }

    /// <summary>
    /// An everyday suggestion is not discarded for sounding vaguely health-adjacent. The markers
    /// are compound phrases and action shapes for exactly this reason — a false discard costs the
    /// caregiver that day's suggestion entirely, since the row is written once a day.
    /// </summary>
    [Theory]
    [InlineData("Warm conditions this week.", "A short walk after lunch is worth trying.")]
    [InlineData("Her sleep has been shorter than usual.", "A steadier bedtime could help her settle.")]
    [InlineData("Steps are down.", "Getting outside for 15 minutes a day is worth a try.")]
    public async Task AnEverydaySuggestion_IsNotDiscarded(string summary, string suggestion)
    {
        _medicalAi.GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdviseGenerationService.AdviseAiResponse
            {
                Summary = summary,
                Suggestion = suggestion,
                GuidelineCited = "WHO adult activity guidance",
            });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.Received(1).AddAsync(Arg.Any<MemberAdvise>());
    }

    /// <summary>
    /// The traceability check the field exists for is that a reference was named, not that the
    /// field was filled. A model that will not leave it blank writes "N/A" instead, which passed a
    /// nonblank check and made an ungrounded suggestion look grounded to every reader of the row.
    /// </summary>
    [Theory]
    [InlineData("N/A")]
    [InlineData("n/a")]
    [InlineData("None")]
    [InlineData("none.")]
    [InlineData("Not applicable")]
    [InlineData("unknown")]
    [InlineData("-")]
    public async Task ACitationNamingNoReference_WithholdsTheRow(string cited)
    {
        _medicalAi.GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdviseGenerationService.AdviseAiResponse
            {
                Summary = "Steps have been below her usual this week.",
                Suggestion = "A short walk after lunch is worth trying.",
                GuidelineCited = cited,
            });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.DidNotReceive().AddAsync(Arg.Any<MemberAdvise>());
    }

    /// <summary>
    /// "None" inside a sentence about the references is an answer about them, not a refusal to name
    /// one — the placeholders are matched whole for this reason.
    /// </summary>
    [Fact]
    public async Task ACitationMentioningNoneInASentence_IsStillACitation()
    {
        _medicalAi.GenerateStructuredAsync<AdviseGenerationService.AdviseAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AdviseGenerationService.AdviseAiResponse
            {
                Summary = "Steps have been below her usual this week.",
                Suggestion = "A short walk after lunch is worth trying.",
                GuidelineCited = "WHO adult activity, none of the sleep references",
            });

        await CreateSut().RegenerateIfDueAsync(_memberId);

        await _advises.Received(1).AddAsync(Arg.Any<MemberAdvise>());
    }

    /// <summary>
    /// The prompt never says "wellness", because MedGemma completes from the nearest text — the
    /// failure <c>CaregiverRegister</c>'s remark documents at length, and the one that put "it's a
    /// general wellness thing that can help with overall feeling" in front of a caregiver. The
    /// words were in the tone block, twice in the instructions, and again as the reference
    /// heading; the boundary they carried is stated in a family's words instead.
    /// </summary>
    [Fact]
    public async Task Prompt_NeverSaysWellness()
    {
        await CreateSut().RegenerateIfDueAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.DoesNotContain("wellness", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
