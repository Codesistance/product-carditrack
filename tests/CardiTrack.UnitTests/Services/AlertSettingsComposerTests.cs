using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The settings rung's replies and proposals, as a pure function of a plan and a snapshot. What
/// a caregiver agrees to is the sentence written here from the request that will actually be
/// sent, so these assert the sentence and the request together.
/// </summary>
public class AlertSettingsComposerTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc);

    private static AlertSettingsSnapshot Snapshot(
        IReadOnlyList<string>? disabledRules = null, params MetricAlarmResponse[] alarms) => new()
    {
        Rules = AlertRuleCatalogue.Clusters
            .SelectMany(c => c.Rules)
            .Select(r => new AlertRuleSettingResponse
            {
                Id = r.Id,
                Title = r.Title,
                Description = r.Description,
                Enabled = !(disabledRules ?? []).Contains(r.Id),
                IsImplemented = r.IsImplemented,
            })
            .ToList(),
        Alarms = alarms
            .Select((a, i) => new AlarmSnapshotEntry(AlertSettingsSnapshot.LabelFor(i), a.Name, a))
            .ToList(),
    };

    private static MetricAlarmResponse HeartRateAlarm(
        bool enabled = true, AlarmProvenance provenance = AlarmProvenance.MemberOnly, string name = "High heart rate") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Metric = AlarmMetric.HeartRate,
        Statistic = AlarmStatistic.Average,
        Operator = AlarmOperator.GreaterThan,
        ThresholdKind = AlarmThresholdKind.Absolute,
        ThresholdValue = 120,
        PeriodMinutes = 5,
        EvaluationPeriods = 2,
        DatapointsToAlarm = 2,
        Severity = AlertSeverity.Orange,
        ContextGate = AlarmContextGate.Inactive,
        IsEnabled = enabled,
        Provenance = provenance,
        Condition = "Average heart rate is above 120 bpm over 5 minutes, on 2 of the last 2, while they are still.",
    };

    [Fact]
    public void DisablingARule_ProposesTheChange_AndAsksForAYes()
    {
        var plan = new AlertChangePlan { Action = AlertChangeAction.DisableRule, RuleId = AlertRuleCatalogue.ActivityDecline };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: true, "Moses", Now);

        Assert.StartsWith("Here's what I'd do: switch off “Activity decline” for Moses — yesterday's steps were well below their usual.",
            reply.Reply, StringComparison.Ordinal);
        Assert.EndsWith(AlertSettingsComposer.ConfirmPrompt, reply.Reply, StringComparison.Ordinal);
        Assert.NotNull(reply.Pending);
        Assert.Equal(PendingAlertChangeKind.SetRule, reply.Pending.Kind);
        Assert.Equal(AlertRuleCatalogue.ActivityDecline, reply.Pending.RuleId);
        Assert.False(reply.Pending.Enabled);
        Assert.Equal(Now, reply.Pending.ProposedAtUtc);
    }

    [Fact]
    public void ARuleAlreadyInThatState_ProposesNothing()
    {
        var plan = new AlertChangePlan { Action = AlertChangeAction.DisableRule, RuleId = AlertRuleCatalogue.ActivityDecline };

        var reply = AlertSettingsComposer.Compose(
            plan, Snapshot(disabledRules: [AlertRuleCatalogue.ActivityDecline]), canManage: true, "Moses", Now);

        Assert.Contains("already off", reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    [Fact]
    public void ARuleNotBuiltYet_CannotBeSwitched()
    {
        var plan = new AlertChangePlan { Action = AlertChangeAction.EnableRule, RuleId = AlertRuleCatalogue.LateBedtime };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: true, "Moses", Now);

        Assert.Contains("isn't available yet", reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    [Fact]
    public void AnUnresolvedRule_AsksWhichOne_NamingOnlyTheBuiltOnes()
    {
        var plan = new AlertChangePlan { Action = AlertChangeAction.DisableRule, RuleId = null };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: true, "Moses", Now);

        Assert.StartsWith("Which of CardiTrack's alerts do you mean?", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("Activity decline", reply.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Late or missed bedtime", reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    /// <summary>"Alert me if his heart rate goes over 120": a reading, a direction and a level,
    /// with the catalogue's suggested shape filled in — a five-minute window, two of two, and the
    /// stillness gate — and the proposal spelling all of it.</summary>
    [Fact]
    public void ANewAlarm_FillsTheSuggestedShape_AndProposesTheSentenceThatWillBeSaved()
    {
        var plan = new AlertChangePlan
        {
            Action = AlertChangeAction.CreateAlarm,
            Metric = AlarmMetric.HeartRate,
            Operator = AlarmOperator.GreaterThan,
            ThresholdValue = 120,
        };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: true, "Moses", Now);

        var pending = Assert.IsType<PendingAlertChange>(reply.Pending);
        Assert.Equal(PendingAlertChangeKind.CreateAlarm, pending.Kind);
        var alarm = pending.Alarm!;
        Assert.Equal("Heart rate above 120 bpm", alarm.Name);
        Assert.Equal(AlarmStatistic.Average, alarm.Statistic);
        Assert.Equal(5, alarm.PeriodMinutes);
        Assert.Equal(2, alarm.EvaluationPeriods);
        Assert.Equal(2, alarm.DatapointsToAlarm);
        Assert.Equal(AlarmContextGate.Inactive, alarm.ContextGate);
        Assert.Equal(AlertSeverity.Yellow, alarm.Severity);
        Assert.False(alarm.ConfirmCriticalSeverity);

        Assert.Contains("“Heart rate above 120 bpm”", reply.Reply, StringComparison.Ordinal);
        Assert.Contains(MetricAlarmNarrative.Condition(pending.Alarm!).TrimEnd('.').ToLowerInvariant()[..20],
            reply.Reply.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("yellow", reply.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain(AlertSettingsComposer.RedSeverityWarning, reply.Reply, StringComparison.Ordinal);
        // The severity gloss closes on a stop of its own; the proposal must not add a second.
        Assert.DoesNotContain("..", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("not an emergency. " + AlertSettingsComposer.ConfirmPrompt, reply.Reply, StringComparison.Ordinal);
    }

    /// <summary>A red alarm's proposal carries the wake-the-family warning, and the yes it asks
    /// for is the builder's explicit confirmation — the request goes out confirmed.</summary>
    [Fact]
    public void ARedAlarm_WarnsWhatRedMeans_AndTheYesIsTheConfirmation()
    {
        var plan = new AlertChangePlan
        {
            Action = AlertChangeAction.CreateAlarm,
            Metric = AlarmMetric.SpO2,
            Operator = AlarmOperator.LessThan,
            ThresholdValue = 90,
            Severity = AlertSeverity.Red,
        };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: true, "Moses", Now);

        Assert.Contains(AlertSettingsComposer.RedSeverityWarning, reply.Reply, StringComparison.Ordinal);
        Assert.True(reply.Pending!.Alarm!.ConfirmCriticalSeverity);
        Assert.Empty(MetricAlarmValidation.Validate(reply.Pending.Alarm));
    }

    [Fact]
    public void ANewAlarmMissingItsLevel_AsksForExactlyThat()
    {
        var plan = new AlertChangePlan
        {
            Action = AlertChangeAction.CreateAlarm,
            Metric = AlarmMetric.HeartRate,
            Operator = AlarmOperator.GreaterThan,
        };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: true, "Moses", Now);

        Assert.Contains("I just need the level", reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    /// <summary>The bradycardia trap: 60 is the textbook line and the floor the builder refuses,
    /// so chat refuses it with the builder's own words rather than proposing an alarm that pages
    /// every night.</summary>
    [Fact]
    public void ALevelOutsideTheBand_IsRefusedWithTheBuildersOwnReason()
    {
        var plan = new AlertChangePlan
        {
            Action = AlertChangeAction.CreateAlarm,
            Metric = AlarmMetric.HeartRate,
            Operator = AlarmOperator.LessThan,
            ThresholdValue = 20,
        };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: true, "Moses", Now);

        Assert.StartsWith("I can't set that one up as it stands: set a level between 30 and 220 bpm.",
            reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    [Fact]
    public void AtTheCeiling_ANewAlarmIsRefusedBeforeItIsProposed()
    {
        var full = Enumerable.Range(0, MetricAlarmValidation.MaxEnabledAlarmsPerMember)
            .Select(i => HeartRateAlarm(name: $"Alarm {i}"))
            .ToArray();
        var plan = new AlertChangePlan
        {
            Action = AlertChangeAction.CreateAlarm,
            Metric = AlarmMetric.HeartRate,
            Operator = AlarmOperator.GreaterThan,
            ThresholdValue = 120,
        };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(null, full), canManage: true, "Moses", Now);

        Assert.Contains("already has 12 alarms switched on", reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    [Fact]
    public void SwitchingOffAnExistingAlarm_ProposesTheRowWithOnlyTheSwitchChanged()
    {
        var row = HeartRateAlarm();
        var plan = new AlertChangePlan { Action = AlertChangeAction.DisableAlarm, AlarmLabel = "alarm-1" };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(null, row), canManage: true, "Moses", Now);

        var pending = Assert.IsType<PendingAlertChange>(reply.Pending);
        Assert.Equal(PendingAlertChangeKind.SaveAlarm, pending.Kind);
        Assert.Equal(row.Id, pending.AlarmId);
        Assert.False(pending.Alarm!.IsEnabled);
        Assert.Equal(row.ThresholdValue, pending.Alarm.ThresholdValue);
        Assert.Equal(row.Severity, pending.Alarm.Severity);
        Assert.Contains("switch off “High heart rate” for Moses", reply.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public void RetuningAnAlarm_KeepsWhatWasNotMentioned()
    {
        var row = HeartRateAlarm();
        var plan = new AlertChangePlan { Action = AlertChangeAction.EditAlarm, AlarmLabel = "alarm-1", ThresholdValue = 130 };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(null, row), canManage: true, "Moses", Now);

        var pending = Assert.IsType<PendingAlertChange>(reply.Pending);
        Assert.Equal(130, pending.Alarm!.ThresholdValue);
        Assert.Equal(row.PeriodMinutes, pending.Alarm.PeriodMinutes);
        Assert.Equal(row.ContextGate, pending.Alarm.ContextGate);
        Assert.Equal(row.Name, pending.Alarm.Name);
        Assert.Contains("130 bpm", reply.Reply, StringComparison.Ordinal);
    }

    /// <summary>Switching an opted-out override back on puts the account's version back — the
    /// alarm service's rule — and the proposal says so rather than letting the tuning vanish.</summary>
    [Fact]
    public void ReenablingAnOptedOutOverride_SaysTheAccountsVersionApplies()
    {
        var row = HeartRateAlarm(enabled: false, provenance: AlarmProvenance.Overridden);
        var plan = new AlertChangePlan { Action = AlertChangeAction.EnableAlarm, AlarmLabel = "alarm-1" };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(null, row), canManage: true, "Moses", Now);

        Assert.Contains("the account's version applies again", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("the account's version applying again", reply.Pending!.Done, StringComparison.Ordinal);
        Assert.Equal(MetricAlarmFingerprint.Of(row), reply.Pending.AlarmFingerprint);
    }

    /// <summary>An inherited default is the account's, not this member's: "get rid of it" is
    /// answered with a switch-off for them, never a proposal to delete a row that is not theirs.</summary>
    [Fact]
    public void DeletingAnInheritedDefault_ProposesSwitchingItOffInstead()
    {
        var row = HeartRateAlarm(provenance: AlarmProvenance.Inherited);
        var plan = new AlertChangePlan { Action = AlertChangeAction.DeleteAlarm, AlarmLabel = "alarm-1" };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(null, row), canManage: true, "Moses", Now);

        var pending = Assert.IsType<PendingAlertChange>(reply.Pending);
        Assert.Equal(PendingAlertChangeKind.SaveAlarm, pending.Kind);
        Assert.False(pending.Alarm!.IsEnabled);
        Assert.Contains("shared across your account", reply.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public void DeletingTheMembersOwnAlarm_ProposesRemovingIt()
    {
        var row = HeartRateAlarm();
        var plan = new AlertChangePlan { Action = AlertChangeAction.DeleteAlarm, AlarmLabel = "alarm-1" };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(null, row), canManage: true, "Moses", Now);

        var pending = Assert.IsType<PendingAlertChange>(reply.Pending);
        Assert.Equal(PendingAlertChangeKind.DeleteAlarm, pending.Kind);
        Assert.Equal(row.Id, pending.AlarmId);
    }

    [Fact]
    public void AnAlarmThatCannotBeResolved_AsksWhichOne_ByName()
    {
        var plan = new AlertChangePlan { Action = AlertChangeAction.EditAlarm, AlarmLabel = null, ThresholdValue = 130 };

        var reply = AlertSettingsComposer.Compose(
            plan, Snapshot(null, HeartRateAlarm(), HeartRateAlarm(name: "Low oxygen")), canManage: true, "Moses", Now);

        Assert.StartsWith("Which alarm do you mean? Moses has “High heart rate” and “Low oxygen”.", reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    /// <summary>A relative invited to watch is told who can change things and given the list —
    /// never a proposal they cannot confirm.</summary>
    [Fact]
    public void ANonPrimaryCaregiver_IsToldWhoCanChangeThings_AndGetsTheList()
    {
        var plan = new AlertChangePlan { Action = AlertChangeAction.DisableRule, RuleId = AlertRuleCatalogue.ActivityDecline };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: false, "Moses", Now);

        Assert.StartsWith("Only Moses's primary caregiver can change what's watching them.", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("CardiTrack's own alerts for Moses — on:", reply.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Ask me to switch", reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    [Fact]
    public void TheList_NamesWhatIsOnAndOff_AndTheAlarmsByCondition()
    {
        var plan = new AlertChangePlan { Action = AlertChangeAction.List };

        var reply = AlertSettingsComposer.Compose(
            plan, Snapshot([AlertRuleCatalogue.ActivityDecline], HeartRateAlarm(enabled: false)), canManage: true, "Moses", Now);

        Assert.Contains("off: Activity decline.", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("Alarms: “High heart rate” — average heart rate is above 120 bpm", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("(off)", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("Ask me to switch any of these", reply.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Late or missed bedtime", reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    [Fact]
    public void QuietHours_AreRedirected_NotChanged()
    {
        var plan = new AlertChangePlan { Action = AlertChangeAction.NotificationSettings };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: true, "Moses", Now);

        Assert.Contains("Settings, then Notifications", reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    [Fact]
    public void TheDoneLine_RepeatsTheProposalsOwnWords()
    {
        var pending = new PendingAlertChange
        {
            Kind = PendingAlertChangeKind.SetRule,
            RuleId = AlertRuleCatalogue.ActivityDecline,
            Enabled = false,
            Summary = "Switch off “Activity decline” for Moses — yesterday's steps were well below their usual",
            Done = "switched off “Activity decline” for Moses",
            ProposedAtUtc = Now,
        };

        Assert.Equal(
            "Done — I've switched off “Activity decline” for Moses. "
            + "You can change it back any time here or from Alert settings.",
            AlertSettingsComposer.AppliedReply(pending));
    }

    /// <summary>Every proposal carries its own past-tense line, so the done reply never reads
    /// "I've switch off" and never sends a caregiver to Alert settings for an alarm just removed.</summary>
    [Fact]
    public void EveryProposal_CarriesItsPastTense_AndTheDoneLineFitsTheKind()
    {
        var row = HeartRateAlarm();
        var snapshot = Snapshot(null, row);

        var removed = AlertSettingsComposer.Compose(
            new AlertChangePlan { Action = AlertChangeAction.DeleteAlarm, AlarmLabel = "alarm-1" }, snapshot, true, "Moses", Now).Pending!;
        Assert.Equal("Done — I've removed the alarm “High heart rate” for Moses. Alert settings shows what applies now.",
            AlertSettingsComposer.AppliedReply(removed));

        var switched = AlertSettingsComposer.Compose(
            new AlertChangePlan { Action = AlertChangeAction.DisableAlarm, AlarmLabel = "alarm-1" }, snapshot, true, "Moses", Now).Pending!;
        Assert.StartsWith("Done — I've switched off “High heart rate” for Moses.", AlertSettingsComposer.AppliedReply(switched), StringComparison.Ordinal);

        var added = AlertSettingsComposer.Compose(
            new AlertChangePlan { Action = AlertChangeAction.CreateAlarm, Metric = AlarmMetric.SleepMinutes, Operator = AlarmOperator.LessThan, ThresholdValue = 300 },
            snapshot, true, "Moses", Now).Pending!;
        Assert.StartsWith("Done — I've added an alarm for Moses called “Sleep duration below 300 minutes”.", AlertSettingsComposer.AppliedReply(added), StringComparison.Ordinal);
    }

    /// <summary>Switching a red alarm back on is agreeing to what red means, so the warning
    /// travels with that proposal as it does with a new red alarm; switching off needs none.</summary>
    [Fact]
    public void ReenablingARedAlarm_CarriesTheRedWarning()
    {
        var red = HeartRateAlarm(enabled: false);
        red = new MetricAlarmResponse
        {
            Id = red.Id, Name = red.Name, Metric = red.Metric, Statistic = red.Statistic, Operator = red.Operator,
            ThresholdKind = red.ThresholdKind, ThresholdValue = red.ThresholdValue, PeriodMinutes = red.PeriodMinutes,
            EvaluationPeriods = red.EvaluationPeriods, DatapointsToAlarm = red.DatapointsToAlarm,
            Severity = AlertSeverity.Red, ContextGate = red.ContextGate, IsEnabled = false,
            Provenance = AlarmProvenance.MemberOnly, Condition = red.Condition,
        };

        var on = AlertSettingsComposer.Compose(
            new AlertChangePlan { Action = AlertChangeAction.EnableAlarm, AlarmLabel = "alarm-1" }, Snapshot(null, red), true, "Moses", Now);
        Assert.Contains(AlertSettingsComposer.RedSeverityWarning, on.Reply, StringComparison.Ordinal);
        Assert.True(on.Pending!.Alarm!.ConfirmCriticalSeverity);

        var redOn = new MetricAlarmResponse
        {
            Id = red.Id, Name = red.Name, Metric = red.Metric, Statistic = red.Statistic, Operator = red.Operator,
            ThresholdKind = red.ThresholdKind, ThresholdValue = red.ThresholdValue, PeriodMinutes = red.PeriodMinutes,
            EvaluationPeriods = red.EvaluationPeriods, DatapointsToAlarm = red.DatapointsToAlarm,
            Severity = AlertSeverity.Red, ContextGate = red.ContextGate, IsEnabled = true,
            Provenance = AlarmProvenance.MemberOnly, Condition = red.Condition,
        };
        var off = AlertSettingsComposer.Compose(
            new AlertChangePlan { Action = AlertChangeAction.DisableAlarm, AlarmLabel = "alarm-1" }, Snapshot(null, redOn), true, "Moses", Now);
        Assert.DoesNotContain(AlertSettingsComposer.RedSeverityWarning, off.Reply, StringComparison.Ordinal);
    }

    /// <summary>A window the caregiver named is kept as named and refused by the builder's rule,
    /// never swapped for the default behind their back.</summary>
    [Fact]
    public void AnUnsupportedWindow_IsRefused_NotSwappedForTheDefault()
    {
        var plan = new AlertChangePlan
        {
            Action = AlertChangeAction.CreateAlarm,
            Metric = AlarmMetric.HeartRate,
            Operator = AlarmOperator.GreaterThan,
            ThresholdValue = 120,
            PeriodMinutes = 7,
        };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: true, "Moses", Now);

        Assert.StartsWith("I can't set that one up as it stands: heart rate can only be watched over", reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    /// <summary>An overlong name is refused in the builder's words rather than saved shortened.</summary>
    [Fact]
    public void AnOverlongName_IsRefused_NotShortened()
    {
        var plan = new AlertChangePlan
        {
            Action = AlertChangeAction.CreateAlarm,
            Metric = AlarmMetric.HeartRate,
            Operator = AlarmOperator.GreaterThan,
            ThresholdValue = 120,
            Name = new string('x', MetricAlarmValidation.MaxNameLength + 1),
        };

        var reply = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: true, "Moses", Now);

        Assert.Contains($"keep the name to {MetricAlarmValidation.MaxNameLength} characters or fewer", reply.Reply, StringComparison.Ordinal);
        Assert.Null(reply.Pending);
    }

    [Fact]
    public void TheList_SaysWhereEachAlarmComesFrom()
    {
        var shared = HeartRateAlarm(provenance: AlarmProvenance.Inherited, name: "Shared one");
        var tuned = HeartRateAlarm(provenance: AlarmProvenance.Overridden, name: "Tuned one");

        var reply = AlertSettingsComposer.Compose(
            new AlertChangePlan { Action = AlertChangeAction.List }, Snapshot(null, shared, tuned), true, "Moses", Now);

        Assert.Contains("“Shared one” — average heart rate is above 120 bpm over 5 minutes, on 2 of the last 2, while they are still (on, shared across the account)", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("“Tuned one” — average heart rate is above 120 bpm over 5 minutes, on 2 of the last 2, while they are still (on, tuned for Moses)", reply.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("you've set", reply.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public void APendingChange_RoundTripsThroughJson_ByName()
    {
        var plan = new AlertChangePlan
        {
            Action = AlertChangeAction.CreateAlarm,
            Metric = AlarmMetric.SleepMinutes,
            Operator = AlarmOperator.LessThan,
            ThresholdValue = 300,
        };
        var pending = AlertSettingsComposer.Compose(plan, Snapshot(), canManage: true, "Moses", Now).Pending!;

        var json = pending.ToJson();
        var back = PendingAlertChange.FromJson(json);

        Assert.Contains("\"CreateAlarm\"", json, StringComparison.Ordinal);
        Assert.Contains("\"SleepMinutes\"", json, StringComparison.Ordinal);
        Assert.NotNull(back);
        Assert.Equal(pending.Kind, back.Kind);
        Assert.Equal(pending.Summary, back.Summary);
        Assert.Equal(300, back.Alarm!.ThresholdValue);
        Assert.Equal(AlarmMetricCatalogue.DailyPeriodMinutes, back.Alarm.PeriodMinutes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"kind\":\"SetRule\",\"summary\":\"x\",\"proposedAtUtc\":\"2026-09-17T10:00:00Z\"}")]
    [InlineData("{\"kind\":\"Nonsense\",\"summary\":\"x\",\"proposedAtUtc\":\"2026-09-17T10:00:00Z\"}")]
    public void AnUnreadableOrIncompleteProposal_IsNoProposal(string? stored) =>
        Assert.Null(PendingAlertChange.FromJson(stored));

    [Fact]
    public void AProposal_StopsBeingCurrent_AfterItsValidity()
    {
        var pending = new PendingAlertChange
        {
            Kind = PendingAlertChangeKind.SetRule,
            RuleId = AlertRuleCatalogue.ActivityDecline,
            Summary = "x",
            ProposedAtUtc = Now,
        };

        Assert.True(pending.IsCurrent(Now + PendingAlertChange.Validity));
        Assert.False(pending.IsCurrent(Now + PendingAlertChange.Validity + TimeSpan.FromSeconds(1)));
    }
}
