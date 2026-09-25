using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// A question about a stretch of days, driven through <see cref="MemberChatService.SendMessageAsync"/>:
/// the averages it rests on are computed in code and handed to the clinical read, a week too thin
/// to average is answered in code without one, and a reply stating an average the nights cannot
/// produce is withheld. From 2026-09-25, when "how did he sleep this week?" was answered twice
/// with about 2h 22m a night under a chart of nights running 4h 18m to 7h.
/// </summary>
public class MemberChatWindowAverageTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();
    private readonly IDataQueryPlanner _planner = Substitute.For<IDataQueryPlanner>();
    private readonly IChatRouter _router = Substitute.For<IChatRouter>();
    private readonly IChatAnswerChecker _checker = Substitute.For<IChatAnswerChecker>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();
    private readonly IActivityLogRepository _activity = Substitute.For<IActivityLogRepository>();
    private readonly IMemberChatSessionRepository _sessions = Substitute.For<IMemberChatSessionRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.UtcNow);

    public MemberChatWindowAverageTests()
    {
        _unitOfWork.CardiMembers.Returns(Substitute.For<ICardiMemberRepository>());
        _unitOfWork.MemberAdvises.Returns(Substitute.For<IMemberAdviseRepository>());
        _unitOfWork.MemberChatSessions.Returns(_sessions);
        _unitOfWork.MemberChatTurns.Returns(Substitute.For<IMemberChatTurnRepository>());
        _unitOfWork.MemberChatTurnUsages.Returns(Substitute.For<IMemberChatTurnUsageRepository>());
        _unitOfWork.ActivityLogs.Returns(_activity);

        _unitOfWork.CardiMembers.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Moses Doe",
            DateOfBirth = new DateOnly(1944, 3, 15),
            IsActive = true,
        });
        _sessions.GetActiveAsync(_userId, _memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((MemberChatSession?)null);

        _rewriteAi.GenerateStructuredWithUsageAsync<MemberChatService.MaliciousCheckAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<MemberChatService.MaliciousCheckAiResponse>(
                new MemberChatService.MaliciousCheckAiResponse
                {
                    IsMalicious = false,
                    IsCasualOrSocial = false,
                    IsOffTopic = false,
                    IsAboutThisMoment = false,
                    IsAskingForAdvice = false,
                },
                new AiUsage { ModelName = "test-rewrite" }));
        _router.RouteAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<ChatRouteDecision>(
                new ChatRouteDecision { Primary = MemberChatWorkflow.Analysis },
                new AiUsage { ModelName = "test-router" }));
        _checker.CheckAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<ChatAnswerAssessment>(
                new ChatAnswerAssessment { Completeness = AnswerCompleteness.Full }, new AiUsage()));
        _planner.PlanAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<DataQueryKind>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<DataQueryPlan>(
                new DataQueryPlan
                {
                    Sources = [DataQueryKind.RecentActivity],
                    RecentActivityDays = 7,
                    ChartMetrics = [ChartMetricKind.Sleep],
                },
                new AiUsage()));
    }

    /// <summary>Invented nights in the reported week's shape, newest last: 4h 48m on average.</summary>
    private void TheWeekIs(params int[] nightsOldestFirst) =>
        _activity.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(nightsOldestFirst
                .Select((minutes, i) => new ActivityLog
                {
                    Date = _today.AddDays(i - nightsOldestFirst.Length + 1),
                    SleepMinutes = minutes,
                })
                .ToList());

    private void TheReadSays(string analysis) =>
        _medicalAi.GenerateStructuredWithUsageAsync<MemberChatService.MemberChatClinicalAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<MemberChatService.MemberChatClinicalAiResponse>(
                new MemberChatService.MemberChatClinicalAiResponse
                {
                    Analysis = analysis,
                    ReadingsFrom = null,
                    ReadingsTo = null,
                },
                new AiUsage()));

    private void TheRewriteSays(string reply) =>
        _rewriteAi.GenerateWithUsageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<string>(reply, new AiUsage()));

    private MemberChatService CreateSut() =>
        new(_medicalAi, _rewriteAi, _planner, _router,
            Substitute.For<IAlertChangePlanner>(), Substitute.For<IAlertPreferenceService>(),
            Substitute.For<IMetricAlarmService>(), _unitOfWork, _access,
            PromptContextFactory.Composer(_unitOfWork), PromptContextFactory.Encryption,
            PromptContextFactory.JournalActions(_rewriteAi, _unitOfWork, _access),
            new PassThroughWriteGuard(),
            NullLogger<MemberChatService>.Instance,
            _checker,
            Microsoft.Extensions.Options.Options.Create(
                new CardiTrack.Infrastructure.Settings.MemberChatOptions { SendBudgetSeconds = 1020 }));

    [Fact]
    public async Task TheClinicalRead_IsHandedTheWeeksAverage_ComputedInCode()
    {
        TheWeekIs(10, 430, 400, 350, 340, 260, 225);
        string? prompt = null;
        _medicalAi.GenerateStructuredWithUsageAsync<MemberChatService.MemberChatClinicalAiResponse>(
                Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<MemberChatService.MemberChatClinicalAiResponse>(
                new MemberChatService.MemberChatClinicalAiResponse
                {
                    Analysis = "Sleep averaged 4h 48m a night across seven nights, below the 7h floor.",
                    ReadingsFrom = null,
                    ReadingsTo = null,
                },
                new AiUsage()));
        TheRewriteSays("CardiTrackCardiMember slept about 4h 48m a night this week, under the recommended 7 hours.");

        var response = await CreateSut().SendMessageAsync(_userId, _memberId, "how did he sleep this week?");

        Assert.NotNull(prompt);
        Assert.Contains("\"average\": \"4h 48m\"", prompt, StringComparison.Ordinal);
        Assert.Contains("slept about 4h 48m a night", response.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reported reply, verbatim in shape: withheld rather than shown, because no night, pair of
    /// nights or yardstick in the fetch comes near 2h 22m.
    /// </summary>
    [Fact]
    public async Task AReplyStatingAnAverageTheNightsCannotProduce_IsWithheld()
    {
        TheWeekIs(10, 430, 400, 350, 340, 260, 225);
        TheReadSays("Sleep averaged 2h 22m a night this week, against a usual of about 6 hours.");
        TheRewriteSays(
            "Over the past week CardiTrackCardiMember slept about 2h 22m a night, compared to "
            + "CardiTrackCardiMemberTheir usual average of nearly 6 hours.");

        var response = await CreateSut().SendMessageAsync(_userId, _memberId, "how did he sleep this week?");

        Assert.Equal(MemberChatService.CouldNotAnswerReply, response.Reply);
    }

    /// <summary>
    /// Three nights of seven: answered in code, with the nights that did arrive, and the clinical
    /// model is never asked to find a week in them.
    /// </summary>
    [Fact]
    public async Task AWeekTooThinToAverage_IsAnsweredInCode_WithoutTheClinicalRead()
    {
        TheWeekIs(340, 260, 225);

        var response = await CreateSut().SendMessageAsync(_userId, _memberId, "how did he sleep this week?");

        Assert.StartsWith("Only 3 of the 7 nights from ", response.Reply, StringComparison.Ordinal);
        Assert.Contains("The nights that did: last night 3h 45m, the night before 4h 20m", response.Reply, StringComparison.Ordinal);
        await _medicalAi.DidNotReceiveWithAnyArgs()
            .GenerateStructuredWithUsageAsync<MemberChatService.MemberChatClinicalAiResponse>(default!, default);
        Assert.NotEmpty(response.Charts);
    }

    /// <summary>
    /// A question about two readings where only one cleared the bar still goes to the clinical
    /// read — and a reply that then gives the thin reading an average is withheld, because the
    /// prompt refused to give one. Steps arrived on every finished day; sleep on three nights.
    /// </summary>
    [Fact]
    public async Task AnAverageOfTooFewNights_IsWithheld_WhenAnotherReadingCarriedTheQuestion()
    {
        _planner.PlanAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<DataQueryKind>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<DataQueryPlan>(
                new DataQueryPlan
                {
                    Sources = [DataQueryKind.RecentActivity],
                    RecentActivityDays = 7,
                    ChartMetrics = [ChartMetricKind.Steps, ChartMetricKind.Sleep],
                },
                new AiUsage()));
        int?[] nights = [null, null, null, null, 400, 250, 225];
        _activity.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(nights
                .Select((minutes, i) => new ActivityLog
                {
                    Date = _today.AddDays(i - nights.Length + 1),
                    Steps = 4000 + (i * 100),
                    SleepMinutes = minutes,
                })
                .ToList());
        TheReadSays("Steps held near usual. Sleep reached us on three nights.");
        TheRewriteSays("CardiTrackCardiMember's sleep averaged 4h 52m a night this week.");

        var response = await CreateSut().SendMessageAsync(_userId, _memberId, "how were his steps and sleep this week?");

        Assert.Equal(MemberChatService.CouldNotAnswerReply, response.Reply);
        await _medicalAi.Received()
            .GenerateStructuredWithUsageAsync<MemberChatService.MemberChatClinicalAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
