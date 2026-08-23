using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="MemberChatService.SendMessageAsync"/> driven through the advice branch end to end,
/// rather than through <see cref="MemberChatService.AdviseReply"/> alone.
/// </summary>
/// <remarks>
/// The helper's own tests cannot see the thing that was actually broken. An advice question
/// reaching the planner is what produced a readback of the week instead of an answer, and a
/// branch-order slip, a renamed triage property or a lost persistence call would restore exactly
/// that failure with every one of those tests still green. So this asserts the routing: the branch
/// is taken, the row is what answers, the three downstream models are never called, and the turn
/// is persisted and billed.
/// </remarks>
public class MemberChatAdviseRoutingTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();
    private readonly IDataQueryPlanner _planner = Substitute.For<IDataQueryPlanner>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();

    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IMemberAdviseRepository _advises = Substitute.For<IMemberAdviseRepository>();
    private readonly IMemberChatSessionRepository _sessions = Substitute.For<IMemberChatSessionRepository>();
    private readonly IMemberChatTurnRepository _turns = Substitute.For<IMemberChatTurnRepository>();
    private readonly IMemberChatTurnUsageRepository _usages = Substitute.For<IMemberChatTurnUsageRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public MemberChatAdviseRoutingTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.MemberAdvises.Returns(_advises);
        _unitOfWork.MemberChatSessions.Returns(_sessions);
        _unitOfWork.MemberChatTurns.Returns(_turns);
        _unitOfWork.MemberChatTurnUsages.Returns(_usages);

        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Moses Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
        });
        _sessions.GetActiveAsync(_userId, _memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((MemberChatSession?)null);
        _advises.GetByCardiMemberAsync(_memberId).Returns(new MemberAdvise
        {
            CardiMemberId = _memberId,
            Summary = "His steps have been below his usual this week.",
            Suggestion = "A short walk after lunch is worth trying.",
            GuidelineCited = "Adult physical activity (WHO, 2020)",
            GeneratedAtUtc = DateTime.UtcNow.AddHours(-6),
        });

        Triage(askingForAdvice: true);
    }

    /// <summary>The one call the advice path does make — the triage that routes it.</summary>
    private void Triage(bool askingForAdvice)
    {
        _rewriteAi.GenerateStructuredWithUsageAsync<MemberChatService.MaliciousCheckAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<MemberChatService.MaliciousCheckAiResponse>(
                new MemberChatService.MaliciousCheckAiResponse
                {
                    IsMalicious = false,
                    IsCasualOrSocial = false,
                    IsOffTopic = false,
                    IsAboutThisMoment = false,
                    IsAskingForAdvice = askingForAdvice,
                },
                new AiUsage { ModelName = "test-rewrite" }));
    }

    private MemberChatService CreateSut() =>
        new(_medicalAi, _rewriteAi, _planner, _unitOfWork, _access,
            PromptContextFactory.Composer(_unitOfWork), PromptContextFactory.Encryption);

    [Fact]
    public async Task AnAdviceQuestion_IsAnsweredFromTheStoredRow()
    {
        var result = await CreateSut().SendMessageAsync(_userId, _memberId, "does he need help with his sleep");

        Assert.Contains("A short walk after lunch is worth trying.", result.Reply, StringComparison.Ordinal);
        Assert.Contains("That's just an idea to consider", result.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure this branch exists to stop. Reaching the planner is what turned "does he need
    /// help with his sleep" into a readback of the week and a sleep chart.
    /// </summary>
    [Fact]
    public async Task AnAdviceQuestion_NeverReachesThePlannerTheClinicalReadOrTheRewrite()
    {
        await CreateSut().SendMessageAsync(_userId, _memberId, "does he need help with his sleep");

        await _planner.DidNotReceive().PlanAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<DataQueryKind>?>(),
            Arg.Any<CancellationToken>());
        await _medicalAi.DidNotReceiveWithAnyArgs()
            .GenerateStructuredWithUsageAsync<MemberChatService.MemberChatClinicalAiResponse>(default!, default);
        await _rewriteAi.DidNotReceive().GenerateWithUsageAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Charts belong to a data answer. An advice reply has no series behind it.</summary>
    [Fact]
    public async Task AnAdviceReply_CarriesNoCharts()
    {
        var result = await CreateSut().SendMessageAsync(_userId, _memberId, "should I be worried about him");

        Assert.Empty(result.Charts);
    }

    /// <summary>
    /// Persisted like any other turn, and billed for the triage that ran — a usage row that skipped
    /// it would make this path look free.
    /// </summary>
    [Fact]
    public async Task TheTurnIsPersistedAndTheTriageIsBilled()
    {
        await CreateSut().SendMessageAsync(_userId, _memberId, "does he need help with his sleep");

        await _turns.Received(1).AddAsync(Arg.Is<MemberChatTurn>(t => t.Role == ChatTurnRole.User));
        await _turns.Received(1).AddAsync(Arg.Is<MemberChatTurn>(t => t.Role == ChatTurnRole.Assistant));
        await _usages.Received(1).AddAsync(Arg.Is<MemberChatTurnUsage>(u =>
            u.Step == AiCallStep.MaliciousCheck && u.ProviderSlot == AiProviderSlot.Rewrite));
        await _unitOfWork.Received().SaveChangesAsync();
    }

    /// <summary>
    /// No current row is not a reason to fall through to the planner — the caregiver asked for a
    /// suggestion, and the honest answer is that there is not one.
    /// </summary>
    [Fact]
    public async Task NoCurrentRow_StillAnswersOnThisPath()
    {
        _advises.GetByCardiMemberAsync(_memberId).Returns((MemberAdvise?)null);

        var result = await CreateSut().SendMessageAsync(_userId, _memberId, "does he need help with his sleep");

        Assert.Contains("don't have a suggestion for Moses", result.Reply, StringComparison.Ordinal);
        await _planner.DidNotReceive().PlanAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<DataQueryKind>?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// And the branch stays off for an ordinary data question, which must still reach the planner.
    /// A flag that fired on everything would be as broken as one that never fired.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryQuestion_StillReachesThePlanner()
    {
        Triage(askingForAdvice: false);
        _planner.PlanAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<DataQueryKind>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<DataQueryPlan>(
                new DataQueryPlan { Sources = [], RecentActivityDays = 7, RealtimeAssessmentHours = 24, ChartMetrics = [] },
                new AiUsage()));
        _medicalAi.GenerateStructuredWithUsageAsync<MemberChatService.MemberChatClinicalAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<MemberChatService.MemberChatClinicalAiResponse>(
                new MemberChatService.MemberChatClinicalAiResponse { Analysis = "Steps are near his usual." },
                new AiUsage()));
        _rewriteAi.GenerateWithUsageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<string>("He's been about as active as usual.", new AiUsage()));

        await CreateSut().SendMessageAsync(_userId, _memberId, "how many steps has he done this week");

        await _planner.Received(1).PlanAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<DataQueryKind>?>(),
            Arg.Any<CancellationToken>());
        await _advises.DidNotReceive().GetByCardiMemberAsync(_memberId);
    }
}
