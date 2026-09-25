using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The chat's <c>settings</c> rung: reading or proposing an alert change, and applying it
/// only on a plain yes. Kept out of <see cref="MemberChatService"/> the same way
/// <see cref="JournalChatActions"/> is — that class routes, persists and bills; this one
/// is the place the alerts are edited.
/// </summary>
public sealed class AlertSettingsChatActions
{
    private readonly IAlertChangePlanner _alertPlanner;
    private readonly IAlertPreferenceService _alertPreferences;
    private readonly IMetricAlarmService _metricAlarms;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;
    private readonly ILogger _logger;

    public AlertSettingsChatActions(
        IAlertChangePlanner alertPlanner,
        IAlertPreferenceService alertPreferences,
        IMetricAlarmService metricAlarms,
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access,
        ILogger logger)
    {
        _alertPlanner = alertPlanner;
        _alertPreferences = alertPreferences;
        _metricAlarms = metricAlarms;
        _unitOfWork = unitOfWork;
        _access = access;
        _logger = logger;
    }

    /// <summary>
    /// Resolve the ask into a closed plan and propose — never apply — the one change it names.
    /// A destructive journal offer still sitting on the session is spent: the last question
    /// the caregiver was asked is the one a yes answers.
    /// </summary>
    public async Task<MemberChatWorkflowResult> HandleAsync(
        string flattened,
        string? questionsOnlyHistory,
        Guid userId,
        Guid cardiMemberId,
        CardiMember? member,
        MemberChatSession session,
        AiUsage triageUsage,
        DateTime utcNow,
        CancellationToken ct)
    {
        var canManage = await MemberChatAccess.CanManageAsync(_access, userId, cardiMemberId, ct);
        var snapshot = await ReadAlertSettingsAsync(userId, cardiMemberId, member?.FullName, ct);

        if (!canManage)
        {
            return new MemberChatWorkflowResult
            {
                Workflow = MemberChatWorkflow.AlertSettings,
                Reply = AlertSettingsComposer.ReadOnlyReply(snapshot, NamePlaceholder.FirstNameOf(member)),
                Calls = [new AiCallRecord(AiCallStep.MaliciousCheck, AiProviderSlot.Rewrite, triageUsage)],
            };
        }

        var planned = await _alertPlanner.PlanAsync(
            NamePlaceholder.RedactMessageOrRefuse(flattened, member?.FullName), questionsOnlyHistory, snapshot, ct);

        var plan = planned.Result;
        if (plan.Name is { } givenName)
        {
            var resolvedName = NamePlaceholder.Resolve(givenName, NamePlaceholder.FirstNameOf(member));
            plan = plan with { Name = NamePlaceholder.IsPresentIn(resolvedName) ? null : resolvedName };
        }

        var composed = AlertSettingsComposer.Compose(
            plan, snapshot, canManage, NamePlaceholder.FirstNameOf(member), utcNow);

        if (composed.Pending is not null && session.PendingAction is not null)
        {
            await _unitOfWork.MemberChatSessions.TryConsumePendingActionAsync(
                session, confirming: false, ct);
        }

        return new MemberChatWorkflowResult
        {
            Workflow = MemberChatWorkflow.AlertSettings,
            Reply = composed.Reply,
            PendingChange = composed.Pending,
            Calls =
            [
                new AiCallRecord(AiCallStep.MaliciousCheck, AiProviderSlot.Rewrite, triageUsage),
                new AiCallRecord(AiCallStep.SettingsPlan, AiProviderSlot.Rewrite, planned.Usage),
            ],
        };
    }

    /// <summary>
    /// The caregiver's yes or no to the change the previous turn proposed. No model runs.
    /// </summary>
    public async Task<MemberChatWorkflowResult> ResolvePendingChangeAsync(
        PendingAlertChange pending,
        Guid pendingTurnId,
        ConfirmationAnswer answer,
        Guid userId,
        Guid cardiMemberId,
        CardiMember? member,
        DateTime utcNow,
        CancellationToken ct)
    {
        _ = member;
        string reply;
        var changed = false;

        if (!await _unitOfWork.MemberChatTurns.TryClaimPendingChangeAsync(pendingTurnId, ct))
        {
            reply = AlertSettingsComposer.AlreadyHandledReply();
        }
        else if (!pending.IsCurrent(utcNow))
        {
            reply = AlertSettingsComposer.LapsedReply();
        }
        else if (answer == ConfirmationAnswer.No)
        {
            reply = AlertSettingsComposer.CancelledReply();
        }
        else
        {
            try
            {
                await ApplyAsync(pending, userId, cardiMemberId, ct);
                _logger.LogInformation(
                    "Alert settings changed via chat: {ChangeKind} for CardiMember {CardiMemberId} by user {UserId}",
                    pending.Kind, cardiMemberId, userId);
                reply = AlertSettingsComposer.AppliedReply(pending);
                changed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is AlertSettingsChangedException or DbUpdateConcurrencyException)
            {
                reply = AlertSettingsComposer.ChangedSinceProposedReply();
            }
            catch (KeyNotFoundException)
            {
                reply = AlertSettingsComposer.CouldNotApplyReply(
                    "it's no longer there, or only the primary caregiver can change it");
            }
            catch (ArgumentException ex)
            {
                reply = AlertSettingsComposer.CouldNotApplyReply(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Applying a confirmed alert-settings change failed for CardiMember {CardiMemberId}",
                    cardiMemberId);
                reply = AlertSettingsComposer.CouldNotApplyReply(null);
            }
        }

        return new MemberChatWorkflowResult
        {
            Workflow = MemberChatWorkflow.AlertSettings,
            Reply = reply,
            Calls = [],
            ChangedAlertSettings = changed,
        };
    }

    private async Task<AlertSettingsSnapshot> ReadAlertSettingsAsync(
        Guid userId, Guid cardiMemberId, string? memberName, CancellationToken ct)
    {
        var overrides = await _alertPreferences.GetOverridesAsync(cardiMemberId, ct);
        var rules = AlertRuleCatalogue.Clusters
            .SelectMany(c => c.Rules)
            .Select(r => new AlertRuleSettingResponse
            {
                Id = r.Id,
                Title = r.Title,
                Description = r.Description,
                Enabled = overrides.IsEnabled(r.Id),
                IsImplemented = r.IsImplemented,
            })
            .ToList();

        var alarms = await _metricAlarms.GetMemberAlarmsAsync(userId, cardiMemberId, ct);

        return new AlertSettingsSnapshot
        {
            Rules = rules,
            RulesFingerprint = overrides.ToJson(),
            Alarms = alarms
                .Select((a, i) => new AlarmSnapshotEntry(
                    AlertSettingsSnapshot.LabelFor(i),
                    NamePlaceholder.Redact(a.Name, memberName) ?? a.Name,
                    a))
                .ToList(),
        };
    }

    private Task ApplyAsync(PendingAlertChange pending, Guid userId, Guid cardiMemberId, CancellationToken ct) =>
        pending.Kind switch
        {
            PendingAlertChangeKind.SetRule =>
                _alertPreferences.SetRuleEnabledAsync(
                    userId, cardiMemberId, pending.RuleId!, pending.Enabled, pending.RulesFingerprint, ct),
            PendingAlertChangeKind.CreateAlarm =>
                _metricAlarms.CreateMemberAlarmAsync(userId, cardiMemberId, pending.Alarm!, ct),
            PendingAlertChangeKind.SaveAlarm =>
                _metricAlarms.SaveMemberOverrideAsync(
                    userId, cardiMemberId, pending.AlarmId!.Value, pending.Alarm!, pending.AlarmFingerprint, ct),
            PendingAlertChangeKind.DeleteAlarm =>
                _metricAlarms.DeleteMemberAlarmAsync(
                    userId, cardiMemberId, pending.AlarmId!.Value, pending.AlarmFingerprint, ct),
            _ => throw new InvalidOperationException("That change is no longer recognised."),
        };
}
