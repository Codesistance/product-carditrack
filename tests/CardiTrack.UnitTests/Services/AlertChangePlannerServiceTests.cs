using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The settings planner: the closed vocabulary it renders, what it must never render, and the
/// defensive parse of what the model answers — the same discipline
/// <see cref="DataQueryPlannerParseTests"/> asserts for the data planner.
/// </summary>
public class AlertChangePlannerServiceTests
{
    private static AlertSettingsSnapshot Snapshot(params MetricAlarmResponse[] alarms) => new()
    {
        Rules = AlertRuleCatalogue.Clusters
            .SelectMany(c => c.Rules)
            .Select(r => new AlertRuleSettingResponse
            {
                Id = r.Id,
                Title = r.Title,
                Description = r.Description,
                Enabled = r.Id != AlertRuleCatalogue.IrregularSleep,
                IsImplemented = r.IsImplemented,
            })
            .ToList(),
        Alarms = alarms
            .Select((a, i) => new AlarmSnapshotEntry(
                AlertSettingsSnapshot.LabelFor(i), NamePlaceholder.Redact(a.Name, "Moses Doe") ?? a.Name, a))
            .ToList(),
    };

    private static MetricAlarmResponse Alarm(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Metric = AlarmMetric.HeartRate,
        Statistic = AlarmStatistic.Average,
        Operator = AlarmOperator.GreaterThan,
        ThresholdValue = 120,
        PeriodMinutes = 5,
        EvaluationPeriods = 2,
        DatapointsToAlarm = 2,
        IsEnabled = true,
        Provenance = AlarmProvenance.MemberOnly,
        Condition = "Average heart rate is above 120 bpm over 5 minutes, on 2 of the last 2.",
    };

    [Fact]
    public void ThePrompt_RendersTheThreeVocabularies_WithTheMembersState()
    {
        var prompt = AlertChangePlannerService.BuildPrompt(
            "turn the sleep one back on", questionsOnlyHistory: null, Snapshot(Alarm("Dad's heart alarm")));

        // The rule catalogue, by id, with this member's switch and the coming-soon marker.
        Assert.Contains($"- {AlertRuleCatalogue.IrregularSleep} — Unusual sleep length:", prompt, StringComparison.Ordinal);
        Assert.Contains("(off)", prompt, StringComparison.Ordinal);
        Assert.Contains($"- {AlertRuleCatalogue.LateBedtime} — Late or missed bedtime:", prompt, StringComparison.Ordinal);
        Assert.Contains("(not available yet)", prompt, StringComparison.Ordinal);

        // The alarm catalogue, by enum name, with its band.
        Assert.Contains("- HeartRate — Heart rate (bpm); windows of 5, 10, 15, 30, 60 minutes", prompt, StringComparison.Ordinal);
        Assert.Contains("level between 30 and 220", prompt, StringComparison.Ordinal);
        Assert.Contains("- RestingHeartRate — Resting heart rate (bpm); one reading per day", prompt, StringComparison.Ordinal);

        // The existing alarm, by label and composed condition.
        Assert.Contains("- alarm-1 — \"", prompt, StringComparison.Ordinal);
        Assert.Contains("Average heart rate is above 120 bpm over 5 minutes", prompt, StringComparison.Ordinal);

        Assert.EndsWith(
            "Omit anything they did not say; the app fills sensible defaults.", prompt.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains(MedicalPromptBlocks.ChatMessageGuardrail, prompt, StringComparison.Ordinal);
    }

    /// <summary>The one caregiver free text in the vocabulary is an alarm's name, and it may well
    /// carry the member's — redacted like every other text that reaches the Rewrite slot. No id
    /// of any kind travels either: the model points at a row by label.</summary>
    [Fact]
    public void ThePrompt_CarriesNoNameAndNoId()
    {
        var alarm = Alarm("Dad's heart alarm — Moses");
        var prompt = AlertChangePlannerService.BuildPrompt("change it to 130", null, Snapshot(alarm));

        Assert.DoesNotContain("Moses", prompt, StringComparison.Ordinal);
        Assert.Contains(NamePlaceholder.Token, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(alarm.Id.ToString(), prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MapsClosedLabels_AndDropsWhatIsNotOffered()
    {
        var snapshot = Snapshot(Alarm("High heart rate"));

        var plan = AlertChangePlannerService.Parse(new AlertChangePlannerService.AlertChangeAiResponse
        {
            Action = "createAlarm",
            RuleId = "not_a_rule",
            AlarmLabel = "alarm-7",
            Metric = "heartrate",
            Operator = "above",
            ThresholdValue = 120,
            ThresholdKind = "level",
            Statistic = "Sum",
            Severity = "Orange",
            WhileStill = true,
            Name = "  Racing heart ",
        }, snapshot);

        Assert.Equal(AlertChangeAction.CreateAlarm, plan.Action);
        Assert.Null(plan.RuleId);
        Assert.Null(plan.AlarmLabel);
        Assert.Equal(AlarmMetric.HeartRate, plan.Metric);
        Assert.Equal(AlarmOperator.GreaterThan, plan.Operator);
        Assert.Equal(120, plan.ThresholdValue);
        Assert.Equal(AlarmThresholdKind.Absolute, plan.ThresholdKind);
        Assert.Equal(AlarmStatistic.Sum, plan.Statistic);
        Assert.Equal(AlertSeverity.Orange, plan.Severity);
        Assert.Equal(AlarmContextGate.Inactive, plan.ContextGate);
        Assert.Equal("Racing heart", plan.Name);
    }

    [Fact]
    public void Parse_KeepsARuleIdTheCatalogueKnows_AndALabelTheSnapshotOffered()
    {
        var snapshot = Snapshot(Alarm("High heart rate"));

        var plan = AlertChangePlannerService.Parse(new AlertChangePlannerService.AlertChangeAiResponse
        {
            Action = "disableRule",
            RuleId = AlertRuleCatalogue.ActivityDecline,
            AlarmLabel = "Alarm-1",
        }, snapshot);

        Assert.Equal(AlertChangeAction.DisableRule, plan.Action);
        Assert.Equal(AlertRuleCatalogue.ActivityDecline, plan.RuleId);
        Assert.Equal("alarm-1", plan.AlarmLabel);
    }

    /// <summary>"999" satisfies TryParse and names nothing, and "1" satisfies it and names the
    /// first member — neither is a label this prompt offered, so neither may become a reading;
    /// an unknown action is unclear rather than a guess at one.</summary>
    [Theory]
    [InlineData("999", "banana", "sometimes")]
    [InlineData("1", "1", "2")]
    [InlineData("", "", "")]
    [InlineData(null, null, null)]
    public void Parse_NeverCoerces(string? action, string? metric, string? severity)
    {
        var plan = AlertChangePlannerService.Parse(new AlertChangePlannerService.AlertChangeAiResponse
        {
            Action = action ?? string.Empty,
            Metric = metric,
            Severity = severity,
            PeriodMinutes = -5,
        }, Snapshot());

        Assert.Equal(AlertChangeAction.Unclear, plan.Action);
        Assert.Null(plan.Metric);
        Assert.Null(plan.Severity);
        Assert.Null(plan.PeriodMinutes);
    }
}
