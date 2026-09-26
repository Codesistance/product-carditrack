using CardiTrack.Application.DTOs.Responses;
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
/// The chat's empty-state chips (decision 2026-09-26): a caregiver with history about this member
/// gets their own last three distinct questions back, newest first; one without gets the
/// standard three. Never another caregiver's questions, never a "yes", always three.
/// </summary>
public class MemberChatSuggestionsTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();
    private readonly IMemberChatSessionRepository _sessions = Substitute.For<IMemberChatSessionRepository>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public MemberChatSuggestionsTests()
    {
        _unitOfWork.CardiMembers.Returns(Substitute.For<ICardiMemberRepository>());
        _unitOfWork.MemberChatSessions.Returns(_sessions);
        _unitOfWork.ActivityLogs.Returns(Substitute.For<IActivityLogRepository>());
        _unitOfWork.Alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns([]);

        // A member with readings this week, so the standard first chip is the watch-out question.
        _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([new ActivityLog { Date = DateOnly.FromDateTime(DateTime.UtcNow), Steps = 4200 }]);

        HistoryIs(_userId);
    }

    private MemberChatService CreateSut() =>
        new(_medicalAi, _rewriteAi, Substitute.For<IDataQueryPlanner>(), Substitute.For<IChatRouter>(),
            Substitute.For<IAlertChangePlanner>(), Substitute.For<IAlertPreferenceService>(),
            Substitute.For<IMetricAlarmService>(), _unitOfWork, _access,
            PromptContextFactory.Composer(_unitOfWork), PromptContextFactory.Encryption,
            PromptContextFactory.JournalActions(_rewriteAi, _unitOfWork, _access),
            new PassThroughWriteGuard(),
            NullLogger<MemberChatService>.Instance,
            Substitute.For<IChatAnswerChecker>(),
            Microsoft.Extensions.Options.Options.Create(new CardiTrack.Infrastructure.Settings.MemberChatOptions()));

    /// <summary>The repository's answer for <paramref name="caregiverId"/> and this member —
    /// questions newest first, encrypted as they are stored.</summary>
    private void HistoryIs(Guid caregiverId, params string[] newestFirst) =>
        _sessions.ListRecentQuestionsAsync(
                caregiverId, _memberId, Arg.Any<IReadOnlyCollection<MemberChatWorkflow>>(),
                Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(newestFirst
                .Select((q, i) => new MemberChatAskedQuestion
                {
                    Content = PromptContextFactory.Encryption.Encrypt(q),
                    AskedAtUtc = DateTime.UtcNow.AddMinutes(-i),
                })
                .ToList());

    private Task<MemberChatSuggestionsResponse> Suggest() => CreateSut().GetSuggestionsAsync(_userId, _memberId);

    [Theory]
    [InlineData(false, "Anything I should keep an eye on?")]
    [InlineData(true, "What's behind the current alert?")]
    public async Task NoHistory_GivesTheStandardThree_WithTheDataDrivenChipFirst(bool unresolvedAlert, string first)
    {
        _unitOfWork.Alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns(
            unresolvedAlert ? [new Alert { CardiMemberId = _memberId }] : []);

        var response = await Suggest();

        Assert.Equal([first, "How are they doing today?", "Show me yesterday's Daybook"], response.Suggestions);
        Assert.Equal(MemberChatSuggestionsResponse.SourceStandard, response.Source);
    }

    [Fact]
    public async Task NoHistory_AndNoReadings_AsksWhetherAnythingHasComeThrough()
    {
        _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);

        var response = await Suggest();

        Assert.Equal(
            ["Has anything come through yet?", "How are they doing today?", "Show me yesterday's Daybook"],
            response.Suggestions);
    }

    /// <summary>Three recent questions fill the row, newest first — and the standard set's alert
    /// and readings reads are never paid for.</summary>
    [Fact]
    public async Task History_OfMoreThanThree_GivesTheNewestThree()
    {
        HistoryIs(_userId,
            "How did she sleep on Tuesday?",
            "Was her heart rate high this morning?",
            "How many steps did she do yesterday?",
            "What was her oxygen like last week?");

        var response = await Suggest();

        Assert.Equal(
            ["How did she sleep on Tuesday?", "Was her heart rate high this morning?", "How many steps did she do yesterday?"],
            response.Suggestions);
        Assert.Equal(MemberChatSuggestionsResponse.SourceRecent, response.Source);
        await _unitOfWork.Alerts.DidNotReceiveWithAnyArgs().GetUnresolvedByCardiMemberAsync(default);
    }

    /// <summary>The same question asked twice is one chip, in its newest wording: case, spacing,
    /// trailing punctuation and a phone's curly apostrophe don't make it a different question.</summary>
    [Fact]
    public async Task History_Repeats_CollapseToTheNewestWording()
    {
        HistoryIs(_userId,
            "how did she sleep last night",
            "How did she   sleep last night?",
            "What’s her heart rate been like?",
            "HOW DID SHE SLEEP LAST NIGHT!!",
            "What's her heart rate been like",
            "Any falls this week?");

        var response = await Suggest();

        Assert.Equal(
            ["how did she sleep last night", "What’s her heart rate been like?", "Any falls this week?"],
            response.Suggestions);
    }

    /// <summary>A "yes" to a pending offer, a bare follow-up, a row of punctuation and a question
    /// too long for a chip are all skipped — never truncated, since a chip is re-sent verbatim.</summary>
    [Fact]
    public async Task History_SkipsConfirmations_FragmentsAndOverlongQuestions()
    {
        HistoryIs(_userId,
            "Yes please",
            "Go ahead.",
            "why?",
            "?????????",
            "no thank you",
            new string('a', 60) + " " + new string('b', 60) + "?",
            "Did she sleep through?");

        var response = await Suggest();

        Assert.Equal(
            ["Did she sleep through?", "Anything I should keep an eye on?", "How are they doing today?"],
            response.Suggestions);
        Assert.Equal(MemberChatSuggestionsResponse.SourceRecent, response.Source);
    }

    /// <summary>Fewer than three recent questions are topped up from the standard three, skipping
    /// one the caregiver has already asked in their own words.</summary>
    [Fact]
    public async Task History_OfFewerThanThree_IsToppedUpFromTheStandardSet_WithoutRepeats()
    {
        HistoryIs(_userId, "how are they doing today", "Did she go out this afternoon?");

        var response = await Suggest();

        Assert.Equal(
            ["how are they doing today", "Did she go out this afternoon?", "Anything I should keep an eye on?"],
            response.Suggestions);
        Assert.Equal(MemberChatSuggestionsResponse.SourceRecent, response.Source);
    }

    /// <summary>Another caregiver of the same member has history; this one has none. Theirs is
    /// never read on this caregiver's behalf, let alone offered.</summary>
    [Fact]
    public async Task AnotherCaregiversHistory_IsNeverUsed()
    {
        var otherCaregiver = Guid.NewGuid();
        HistoryIs(otherCaregiver, "Is his blood pressure medication working?");

        var response = await Suggest();

        Assert.Equal(MemberChatSuggestionsResponse.SourceStandard, response.Source);
        Assert.DoesNotContain("Is his blood pressure medication working?", response.Suggestions);
        await _sessions.Received(1).ListRecentQuestionsAsync(
            _userId, _memberId, Arg.Any<IReadOnlyCollection<MemberChatWorkflow>>(),
            Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _sessions.DidNotReceive().ListRecentQuestionsAsync(
            otherCaregiver, Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<MemberChatWorkflow>>(),
            Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Only questions a reading or acting rung answered qualify — never a steer or a
    /// clarify — read over a bounded window.</summary>
    [Fact]
    public async Task History_IsReadForTheReadingAndActingRungs_OverABoundedWindow()
    {
        IReadOnlyCollection<MemberChatWorkflow>? answeredBy = null;
        DateTime since = default;
        var limit = 0;
        _sessions.ListRecentQuestionsAsync(
                _userId, _memberId,
                Arg.Do<IReadOnlyCollection<MemberChatWorkflow>>(w => answeredBy = w),
                Arg.Do<DateTime>(d => since = d), Arg.Do<int>(l => limit = l), Arg.Any<CancellationToken>())
            .Returns([]);

        await Suggest();

        Assert.NotNull(answeredBy);
        Assert.Equal(
            new[]
            {
                MemberChatWorkflow.Status, MemberChatWorkflow.Analysis, MemberChatWorkflow.Inference,
                MemberChatWorkflow.Investigation, MemberChatWorkflow.Advise, MemberChatWorkflow.Journal,
                MemberChatWorkflow.AlertSettings,
            }.Order(),
            answeredBy.Order());
        Assert.InRange(since, DateTime.UtcNow.AddDays(-91), DateTime.UtcNow.AddDays(-89));
        Assert.Equal(MemberChatService.RecentQuestionScanLimit, limit);
    }

    [Fact]
    public async Task ACaregiverWhoMayNotViewTheMember_IsRefused_BeforeAnyHistoryIsRead()
    {
        _access.RequireViewAccessAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException("We couldn't find what you were looking for."));

        await Assert.ThrowsAsync<KeyNotFoundException>(Suggest);

        await _sessions.DidNotReceiveWithAnyArgs().ListRecentQuestionsAsync(
            default, default, default!, default, default, default);
    }
}
