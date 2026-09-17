using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The settings rung end to end through <see cref="MemberChatService.SendMessageAsync"/>: a routed
/// request proposes and persists a change without applying it, the caregiver's yes on the next
/// turn applies exactly that through the alert services with no model in the loop, and every
/// refusal is a reply rather than a failed send.
/// </summary>
public class MemberChatAlertSettingsRungTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();
    private readonly IDataQueryPlanner _planner = Substitute.For<IDataQueryPlanner>();
    private readonly IChatRouter _router = Substitute.For<IChatRouter>();
    private readonly IAlertChangePlanner _alertPlanner = Substitute.For<IAlertChangePlanner>();
    private readonly IAlertPreferenceService _alertPreferences = Substitute.For<IAlertPreferenceService>();
    private readonly IMetricAlarmService _metricAlarms = Substitute.For<IMetricAlarmService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();

    private readonly IMemberChatSessionRepository _sessions = Substitute.For<IMemberChatSessionRepository>();
    private readonly IMemberChatTurnRepository _turns = Substitute.For<IMemberChatTurnRepository>();
    private readonly IMemberChatTurnUsageRepository _usages = Substitute.For<IMemberChatTurnUsageRepository>();
    private readonly List<MemberChatTurn> _persisted = [];

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public MemberChatAlertSettingsRungTests()
    {
        _unitOfWork.CardiMembers.Returns(Substitute.For<ICardiMemberRepository>());
        _unitOfWork.MemberAdvises.Returns(Substitute.For<IMemberAdviseRepository>());
        _unitOfWork.MemberChatSessions.Returns(_sessions);
        _unitOfWork.MemberChatTurns.Returns(_turns);
        _unitOfWork.MemberChatTurnUsages.Returns(_usages);
        _unitOfWork.ActivityLogs.Returns(Substitute.For<IActivityLogRepository>());
        _turns.AddAsync(Arg.Do<MemberChatTurn>(_persisted.Add));

        _unitOfWork.CardiMembers.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Moses Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
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

        // Every rule on, no alarms — the state most members are in.
        _alertPreferences.GetOverridesAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(AlertRuleOverrides.AllEnabled);
        _metricAlarms.GetMemberAlarmsAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MetricAlarmResponse>)[]);
    }

    private void RouterAnswers(MemberChatWorkflow? primary, MemberChatWorkflow? runnerUp = null) =>
        _router.RouteAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<ChatRouteDecision>(
                new ChatRouteDecision { Primary = primary, RunnerUp = runnerUp },
                new AiUsage { ModelName = "test-router" }));

    private void PlannerAnswers(AlertChangePlan plan) =>
        _alertPlanner.PlanAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<AlertSettingsSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<AlertChangePlan>(plan, new AiUsage { ModelName = "test-planner" }));

    /// <summary>A session whose most recent reply proposed switching off activity decline.</summary>
    private void AProposalIsPending(TimeSpan age)
    {
        var pending = new PendingAlertChange
        {
            Kind = PendingAlertChangeKind.SetRule,
            RuleId = AlertRuleCatalogue.ActivityDecline,
            Enabled = false,
            Summary = "Switch off “Activity decline” for Moses — yesterday's steps were well below their usual",
            Done = "switched off “Activity decline” for Moses",
            ProposedAtUtc = DateTime.UtcNow - age,
        };
        var session = new MemberChatSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            CardiMemberId = _memberId,
            StartedAtUtc = DateTime.UtcNow - age - TimeSpan.FromMinutes(1),
            LastTurnAtUtc = DateTime.UtcNow - age,
        };
        session.Turns.Add(new MemberChatTurn
        {
            SessionId = session.Id,
            Role = ChatTurnRole.User,
            Content = PromptContextFactory.Encryption.Encrypt("turn off the activity decline alert"),
            CreatedAtUtc = DateTime.UtcNow - age,
        });
        session.Turns.Add(new MemberChatTurn
        {
            Id = _pendingTurnId,
            SessionId = session.Id,
            Role = ChatTurnRole.Assistant,
            Workflow = MemberChatWorkflow.AlertSettings,
            Content = PromptContextFactory.Encryption.Encrypt("Here's what I'd do: …"),
            PendingChange = PromptContextFactory.Encryption.Encrypt(pending.ToJson()),
            CreatedAtUtc = DateTime.UtcNow - age,
        });
        _sessions.GetActiveAsync(_userId, _memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(session);
        _sessions.GetByIdWithTurnsAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        // The claim succeeds: this answer is the first to take the proposal.
        _turns.TryClaimPendingChangeAsync(_pendingTurnId, Arg.Any<CancellationToken>()).Returns(true);
    }

    private readonly Guid _pendingTurnId = Guid.NewGuid();

    private MemberChatService CreateSut() =>
        new(_medicalAi, _rewriteAi, _planner, _router, _alertPlanner, _alertPreferences, _metricAlarms,
            _unitOfWork, _access, PromptContextFactory.Composer(_unitOfWork), PromptContextFactory.Encryption,
            NullLogger<MemberChatService>.Instance);

    [Fact]
    public async Task ASettingsRoute_ProposesTheChange_PersistsItOnTheTurn_AndAppliesNothing()
    {
        RouterAnswers(MemberChatWorkflow.AlertSettings);
        PlannerAnswers(new AlertChangePlan { Action = AlertChangeAction.DisableRule, RuleId = AlertRuleCatalogue.ActivityDecline });

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "turn off the activity decline alert");

        Assert.StartsWith("Here's what I'd do: switch off “Activity decline” for Moses", reply.Reply, StringComparison.Ordinal);
        Assert.EndsWith(AlertSettingsComposer.ConfirmPrompt, reply.Reply, StringComparison.Ordinal);
        Assert.False(reply.ChangedAlertSettings);

        // Proposed, not applied.
        await _alertPreferences.DidNotReceiveWithAnyArgs().SetRuleEnabledAsync(default, default, default!, default, default);

        // The proposal rides on the assistant turn, encrypted, exactly as shown.
        var assistant = Assert.Single(_persisted, t => t.Role == ChatTurnRole.Assistant);
        Assert.Equal(MemberChatWorkflow.AlertSettings, assistant.Workflow);
        var stored = PendingAlertChange.FromJson(PromptContextFactory.Encryption.Decrypt(assistant.PendingChange!));
        Assert.NotNull(stored);
        Assert.Equal(PendingAlertChangeKind.SetRule, stored.Kind);
        Assert.Equal(AlertRuleCatalogue.ActivityDecline, stored.RuleId);
        Assert.False(stored.Enabled);

        // Two calls after the route: the pre-check and the plan. No clinical read, no rewrite.
        await _usages.Received().AddAsync(Arg.Is<MemberChatTurnUsage>(u => u.Step == AiCallStep.SettingsPlan));
        await _usages.Received().AddAsync(Arg.Is<MemberChatTurnUsage>(u => u.Step == AiCallStep.Route));
        await _medicalAi.DidNotReceiveWithAnyArgs()
            .GenerateStructuredWithUsageAsync<MemberChatService.MemberChatClinicalAiResponse>(default!, default);
        await _planner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default, default, default);
    }

    /// <summary>The planner sees the member's alarms by label with the name redacted, and the
    /// caregiver's prior questions only — never the model's own prior prose.</summary>
    [Fact]
    public async Task ThePlanner_IsHandedTheSnapshot_WithNamesRedacted()
    {
        RouterAnswers(MemberChatWorkflow.AlertSettings);
        PlannerAnswers(new AlertChangePlan { Action = AlertChangeAction.List });
        _metricAlarms.GetMemberAlarmsAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MetricAlarmResponse>)[new MetricAlarmResponse
            {
                Id = Guid.NewGuid(),
                Name = "Moses's heart alarm",
                Metric = AlarmMetric.HeartRate,
                Condition = "Average heart rate is above 120 bpm over 5 minutes.",
                IsEnabled = true,
                Provenance = AlarmProvenance.MemberOnly,
            }]);

        await CreateSut().SendMessageAsync(_userId, _memberId, "which alerts are on?");

        var snapshot = (AlertSettingsSnapshot)_alertPlanner.ReceivedCalls().Single().GetArguments()[2]!;
        var entry = Assert.Single(snapshot.Alarms);
        Assert.Equal("alarm-1", entry.Label);
        Assert.DoesNotContain("Moses", entry.RedactedName, StringComparison.Ordinal);
        Assert.Equal("Moses's heart alarm", entry.Row.Name);
        Assert.Contains(snapshot.Rules, r => r.Id == AlertRuleCatalogue.ActivityDecline && r.Enabled);
    }

    [Fact]
    public async Task AYesAfterAProposal_AppliesIt_WithNoModelInTheLoop()
    {
        AProposalIsPending(age: TimeSpan.FromMinutes(1));

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "Yes please");

        await _alertPreferences.Received(1).SetRuleEnabledAsync(
            _userId, _memberId, AlertRuleCatalogue.ActivityDecline, false, Arg.Any<CancellationToken>());
        Assert.StartsWith("Done — I've switched off “Activity decline” for Moses.", reply.Reply, StringComparison.Ordinal);
        Assert.True(reply.ChangedAlertSettings);

        // The proposal was claimed before it was applied — the atomic step a retried yes fails.
        await _turns.Received(1).TryClaimPendingChangeAsync(_pendingTurnId, Arg.Any<CancellationToken>());

        // Not the pre-check, not the router, not the planner: the app knew what "yes" was.
        await _rewriteAi.DidNotReceiveWithAnyArgs()
            .GenerateStructuredWithUsageAsync<MemberChatService.MaliciousCheckAiResponse>(default!, default);
        await _router.DidNotReceiveWithAnyArgs().RouteAsync(default!, default, default);
        await _alertPlanner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default, default!, default);
        await _usages.DidNotReceiveWithAnyArgs().AddAsync(default!);

        // The turn is stamped with the rung, and carries no new proposal.
        var assistant = Assert.Single(_persisted, t => t.Role == ChatTurnRole.Assistant);
        Assert.Equal(MemberChatWorkflow.AlertSettings, assistant.Workflow);
        Assert.Null(assistant.PendingChange);
    }

    /// <summary>A yes the phone sent twice, or two racing: the second finds the proposal
    /// already taken and applies nothing — the idempotency the alarm service itself lacks.</summary>
    [Fact]
    public async Task AYesThatFindsTheProposalAlreadyClaimed_AppliesNothing()
    {
        AProposalIsPending(age: TimeSpan.FromMinutes(1));
        _turns.TryClaimPendingChangeAsync(_pendingTurnId, Arg.Any<CancellationToken>()).Returns(false);

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "yes");

        Assert.Equal(AlertSettingsComposer.AlreadyHandledReply(), reply.Reply);
        Assert.False(reply.ChangedAlertSettings);
        await _alertPreferences.DidNotReceiveWithAnyArgs().SetRuleEnabledAsync(default, default, default!, default, default);
    }

    /// <summary>The question itself is redacted like the recalled history before it reaches the
    /// planner — the one text on this call a caregiver can put the member's name in.</summary>
    [Fact]
    public async Task ThePlanner_NeverSeesTheMembersName_InTheQuestion()
    {
        RouterAnswers(MemberChatWorkflow.AlertSettings);
        PlannerAnswers(new AlertChangePlan { Action = AlertChangeAction.List });

        await CreateSut().SendMessageAsync(_userId, _memberId, "which alerts are on for Moses?");

        var question = (string)_alertPlanner.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.DoesNotContain("Moses", question, StringComparison.Ordinal);
        Assert.Contains(NamePlaceholder.Token, question, StringComparison.Ordinal);
    }

    /// <summary>The name the model hands back may carry the placeholder it was shown — "turn
    /// on an alarm called Moses's heart" reaches it redacted — and that token must be the first
    /// name again before it is saved or shown, never the placeholder itself.</summary>
    [Fact]
    public async Task AnAlarmNameTheModelEchoes_HasThePlaceholderResolved_BeforeItIsProposed()
    {
        RouterAnswers(MemberChatWorkflow.AlertSettings);
        PlannerAnswers(new AlertChangePlan
        {
            Action = AlertChangeAction.CreateAlarm,
            Metric = AlarmMetric.HeartRate,
            Operator = AlarmOperator.GreaterThan,
            ThresholdValue = 120,
            Name = $"{NamePlaceholder.Token}'s racing heart",
        });

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "alert me if Moses's heart goes over 120, call it Moses's racing heart");

        Assert.Contains("“Moses's racing heart”", reply.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain(NamePlaceholder.Token, reply.Reply, StringComparison.Ordinal);
        var assistant = Assert.Single(_persisted, t => t.Role == ChatTurnRole.Assistant);
        var stored = PendingAlertChange.FromJson(PromptContextFactory.Encryption.Decrypt(assistant.PendingChange!));
        Assert.Equal("Moses's racing heart", stored!.Alarm!.Name);
    }

    [Fact]
    public async Task ANoAfterAProposal_ChangesNothing()
    {
        AProposalIsPending(age: TimeSpan.FromMinutes(1));

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "no, leave it");

        Assert.Equal(AlertSettingsComposer.CancelledReply(), reply.Reply);
        Assert.False(reply.ChangedAlertSettings);
        await _alertPreferences.DidNotReceiveWithAnyArgs().SetRuleEnabledAsync(default, default, default!, default, default);
        await _router.DidNotReceiveWithAnyArgs().RouteAsync(default!, default, default);
        // A no takes the proposal too, so a later yes has nothing to apply.
        await _turns.Received(1).TryClaimPendingChangeAsync(_pendingTurnId, Arg.Any<CancellationToken>());
    }

    /// <summary>A caregiver who reopens an old thread and types "yes" must not switch off an
    /// alert they had forgotten was proposed.</summary>
    [Fact]
    public async Task AYesAfterTheProposalLapsed_ChangesNothing()
    {
        AProposalIsPending(age: PendingAlertChange.Validity + TimeSpan.FromMinutes(5));

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "yes");

        Assert.Equal(AlertSettingsComposer.LapsedReply(), reply.Reply);
        await _alertPreferences.DidNotReceiveWithAnyArgs().SetRuleEnabledAsync(default, default, default!, default, default);
    }

    /// <summary>Anything that is not a plain yes or no is a new message: it routes, and the
    /// proposal is superseded by whatever answers it.</summary>
    [Fact]
    public async Task AQuestionAfterAProposal_RoutesNormally_AndTheProposalLapses()
    {
        AProposalIsPending(age: TimeSpan.FromMinutes(1));
        RouterAnswers(MemberChatWorkflow.SteerCasual);
        _rewriteAi.GenerateStructuredWithUsageAsync<MemberChatService.SteerAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<MemberChatService.SteerAiResponse>(
                new MemberChatService.SteerAiResponse { Reply = "Happy to help." }, new AiUsage()));

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "yes but only at night, thanks");

        Assert.Equal("Happy to help.", reply.Reply);
        await _router.Received(1).RouteAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _alertPreferences.DidNotReceiveWithAnyArgs().SetRuleEnabledAsync(default, default, default!, default, default);
        var assistant = Assert.Single(_persisted, t => t.Role == ChatTurnRole.Assistant);
        Assert.Null(assistant.PendingChange);
    }

    /// <summary>A relative invited to watch reaches chat on view access alone. They are told who
    /// can change things and given the list — never a proposal they could not confirm.</summary>
    [Fact]
    public async Task ANonPrimaryCaregiver_IsToldWhoCanChangeThings_AndNothingIsProposed()
    {
        _access.When(a => a.RequireManageAccessAsync(_userId, _memberId, Arg.Any<CancellationToken>()))
            .Do(_ => throw new KeyNotFoundException("CardiMember not found"));
        RouterAnswers(MemberChatWorkflow.AlertSettings);
        PlannerAnswers(new AlertChangePlan { Action = AlertChangeAction.DisableRule, RuleId = AlertRuleCatalogue.ActivityDecline });

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "turn off the activity decline alert");

        Assert.StartsWith("Only Moses's primary caregiver can change what's watching them.", reply.Reply, StringComparison.Ordinal);
        var assistant = Assert.Single(_persisted, t => t.Role == ChatTurnRole.Assistant);
        Assert.Null(assistant.PendingChange);
    }

    /// <summary>The service re-checks authority at apply time; a refusal there is a reply, never a
    /// 404 on a send whose whole content was "yes".</summary>
    [Fact]
    public async Task ARefusedApply_IsReported_NotThrown()
    {
        AProposalIsPending(age: TimeSpan.FromMinutes(1));
        _alertPreferences.SetRuleEnabledAsync(
                _userId, _memberId, AlertRuleCatalogue.ActivityDecline, false, Arg.Any<CancellationToken>())
            .Returns<Task<AlertRuleSettingResponse>>(_ => throw new KeyNotFoundException("CardiMember not found"));

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "yes");

        Assert.StartsWith("I couldn't make that change:", reply.Reply, StringComparison.Ordinal);
        Assert.False(reply.ChangedAlertSettings);
    }

    /// <summary>A refusal the builder wrote for a caregiver is repeated to them; any other
    /// failure gets the generic line, never its message.</summary>
    [Fact]
    public async Task ABuildersRefusal_IsRepeated_ButAnUnexpectedFailureIsNot()
    {
        AProposalIsPending(age: TimeSpan.FromMinutes(1));
        _alertPreferences.SetRuleEnabledAsync(
                _userId, _memberId, AlertRuleCatalogue.ActivityDecline, false, Arg.Any<CancellationToken>())
            .Returns<Task<AlertRuleSettingResponse>>(_ => throw new ArgumentException("Unknown alert rule."));
        var refused = await CreateSut().SendMessageAsync(_userId, _memberId, "yes");
        Assert.Equal("I couldn't make that change: Unknown alert rule. Nothing has been altered.", refused.Reply);

        _persisted.Clear();
        AProposalIsPending(age: TimeSpan.FromMinutes(1));
        _alertPreferences.SetRuleEnabledAsync(
                _userId, _memberId, AlertRuleCatalogue.ActivityDecline, false, Arg.Any<CancellationToken>())
            .Returns<Task<AlertRuleSettingResponse>>(_ => throw new InvalidOperationException("connection string 'Host=10.0.0.4' rejected"));
        var failed = await CreateSut().SendMessageAsync(_userId, _memberId, "yes");
        Assert.Equal(AlertSettingsComposer.CouldNotApplyReply(null), failed.Reply);
        Assert.DoesNotContain("10.0.0.4", failed.Reply, StringComparison.Ordinal);
        Assert.False(failed.ChangedAlertSettings);
    }

    /// <summary>Settings against a steer: a redirect never beats a request the app can serve.</summary>
    [Theory]
    [InlineData(MemberChatWorkflow.SteerOffTopic, MemberChatWorkflow.AlertSettings)]
    [InlineData(MemberChatWorkflow.AlertSettings, MemberChatWorkflow.SteerCasual)]
    public async Task SettingsAgainstASteer_RunsSettings(MemberChatWorkflow primary, MemberChatWorkflow runnerUp)
    {
        RouterAnswers(primary, runnerUp);
        PlannerAnswers(new AlertChangePlan { Action = AlertChangeAction.List });

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "what alerts are on for dad");

        Assert.StartsWith("CardiTrack's own alerts for Moses — on:", reply.Reply, StringComparison.Ordinal);
        await _rewriteAi.DidNotReceiveWithAnyArgs()
            .GenerateStructuredWithUsageAsync<MemberChatService.SteerAiResponse>(default!, default);
    }

    /// <summary>"Is his heart alert on" against "is his heart okay" is a genuinely different ask,
    /// so a settings-against-reading pair asks which was meant.</summary>
    [Fact]
    public async Task SettingsAgainstAReadingRung_AsksWhichWasMeant()
    {
        RouterAnswers(MemberChatWorkflow.AlertSettings, MemberChatWorkflow.Inference);

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "is his heart alert on?");

        Assert.Contains("Which would help most?", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("changing or checking which alerts are on", reply.Reply, StringComparison.Ordinal);
        await _alertPlanner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default, default!, default);
    }
}
