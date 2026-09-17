using System.ComponentModel;
using System.Globalization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Reads an alert-settings message into an <see cref="AlertChangePlan"/> on the Rewrite slot —
/// the settings rung's one model call, modelled on <see cref="DataQueryPlannerService"/>: a
/// closed vocabulary rendered into the prompt and used again to gate the parse, so what the model
/// is offered and what the code accepts are one list.
/// </summary>
/// <remarks>
/// <para>
/// What the prompt carries: the caregiver's message and prior questions, name-redacted like every
/// Rewrite-slot payload; the rule catalogue's ids, titles and descriptions with on/off for this
/// member; the alarm metric catalogue's readings, units, statistics, windows and level bands; and
/// the member's existing alarms under positional labels with their composed condition sentence.
/// No id of any kind, no reading values, no member context — an alarm's name is the one piece of
/// caregiver free text here, and it travels redacted. Which rule is on for a person is a fact
/// about their monitoring rather than their body, and it is the fact the model needs to answer
/// "turn the sleep one back on".
/// </para>
/// <para>
/// What the model may say: labels from those lists and numbers. Nothing it writes is shown to a
/// caregiver; <see cref="AlertSettingsComposer"/> writes every sentence from what parsed, and
/// <see cref="MetricAlarmValidation"/> refuses what the builder would refuse.
/// </para>
/// </remarks>
public class AlertChangePlannerService : IAlertChangePlanner
{
    /// <summary>
    /// The brief, exposed for the claim-class parity test: the settings rung claims nothing about
    /// the member, so this carries no tone or clinical block — only the vocabulary, the job, and
    /// the guardrail every Rewrite-slot prompt carries over the caregiver's words.
    /// </summary>
    internal const string Instructions = """
        A family caregiver sent the message below inside a health-monitoring app, and it is about
        the app's alerts for their family member: which are on, or a change to them. Work out what
        they are asking for, using only the vocabulary given here. Do not answer them, and do not
        invent a rule, reading or alarm that is not listed.

        There are two kinds of alert. The app's own alerts are fixed rules a caregiver can only
        switch on or off, listed by id. Alarms are ones the caregiver builds on a reading — a
        level, a direction, a window — and can be added, changed, switched, or removed; the ones
        already set are listed by label.
        """ + MedicalPromptBlocks.ChatMessageGuardrail;

    private const string Actions =
        "list, enableRule, disableRule, createAlarm, editAlarm, deleteAlarm, enableAlarm, "
        + "disableAlarm, notificationSettings, unclear";

    private readonly IRewriteAiService _rewriteAi;

    public AlertChangePlannerService(IRewriteAiService rewriteAi) => _rewriteAi = rewriteAi;

    public async Task<AiGenerationResult<AlertChangePlan>> PlanAsync(
        string question,
        string? questionsOnlyHistory,
        AlertSettingsSnapshot snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var prompt = BuildPrompt(question, questionsOnlyHistory, snapshot);
        var result = await _rewriteAi.GenerateStructuredWithUsageAsync<AlertChangeAiResponse>(prompt, ct);

        return new AiGenerationResult<AlertChangePlan>(Parse(result.Result, snapshot), result.Usage);
    }

    internal static string BuildPrompt(string question, string? questionsOnlyHistory, AlertSettingsSnapshot snapshot)
    {
        var rules = string.Join("\n", snapshot.Rules.Select(r =>
            $"- {r.Id} — {r.Title}: {r.Description}"
            + (r.IsImplemented ? $" ({(r.Enabled ? "on" : "off")})" : " (not available yet)")));

        var readings = string.Join("\n", AlarmMetricCatalogue.Definitions.Select(d =>
            $"- {d.Metric} — {d.Title} ({d.Unit}); "
            + (d.Source == AlarmMetricSource.Daily
                ? "one reading per day"
                : $"windows of {string.Join(", ", d.PeriodMinutes)} minutes; "
                  + $"statistics {string.Join(", ", d.Statistics)}")
            + $"; level between {Number(d.MinThreshold)} and {Number(d.MaxThreshold)}"
            + (d.SupportsBaselinePercent ? "; can be a percent of their usual" : string.Empty)
            + (d.SupportsBaselineSigma ? "; can be a departure from their usual" : string.Empty)
            + (d.Source == AlarmMetricSource.Granular ? "; can be limited to while they are still" : string.Empty)));

        var alarms = snapshot.Alarms.Count == 0
            ? "(none set yet)"
            : string.Join("\n", snapshot.Alarms.Select(a =>
                $"- {a.Label} — \"{a.RedactedName}\": {a.Row.Condition} ({(a.Row.IsEnabled ? "on" : "off")})"));

        var historySection = questionsOnlyHistory is null
            ? string.Empty
            : $"""

              The message may be a short follow-up — read it against what the caregiver already
              asked below.

              {questionsOnlyHistory}
              """;

        return $"""
            {Instructions}

            The app's own alerts (id — name: what it watches; whether it is on for this person):
            {rules}

            Readings an alarm can be built on (reading — name (unit); how it is watched; the level it accepts):
            {readings}

            Alarms already set for this person (label — name: condition; on or off):
            {alarms}
            {historySection}

            --- {MedicalPromptBlocks.ChatQuestionLabel} ---
            {question}

            Respond with:
            - action: exactly one of {Actions}. Use list when they ask what is on. Use
              notificationSettings for quiet hours, muting, or how alerts reach their phone. Use
              unclear when the message is about alerts but names nothing above.
            - ruleId: for enableRule or disableRule, the id of the app's own alert they mean,
              exactly as listed. Omit otherwise.
            - alarmLabel: for editAlarm, deleteAlarm, enableAlarm or disableAlarm, the label of the
              alarm they mean, exactly as listed. Omit otherwise, and omit when none of the listed
              alarms is clearly the one meant.
            - metric: for createAlarm or editAlarm, the reading, exactly as listed. Omit when they
              did not name one. Heart rate during the day is HeartRate; overnight or resting figures
              are the daily readings.
            - operator: above, atOrAbove, below, or atOrBelow — which side of the level alerts.
            - thresholdValue: the level as a number, in the reading's unit, or the percent when the
              level is a share of their usual.
            - thresholdKind: level, percentOfUsual, or departureFromUsual. Omit for a plain level.
            - statistic, periodMinutes, evaluationPeriods, datapointsToAlarm, severity (yellow,
              orange or red), whileStill (true or false), name: only when the caregiver said so.
              Omit anything they did not say; the app fills sensible defaults.
            """;
    }

    /// <summary>
    /// Unrecognised labels drop to null, never throw and never coerce — the enum's own names for
    /// the enums, the catalogue for rule ids, the snapshot for alarm labels — the
    /// same discipline <see cref="DataQueryPlannerService.Parse"/> applies.
    /// </summary>
    internal static AlertChangePlan Parse(AlertChangeAiResponse response, AlertSettingsSnapshot snapshot)
    {
        var action = Canonical(response.Action) switch
        {
            "list" => AlertChangeAction.List,
            "enablerule" => AlertChangeAction.EnableRule,
            "disablerule" => AlertChangeAction.DisableRule,
            "createalarm" => AlertChangeAction.CreateAlarm,
            "editalarm" => AlertChangeAction.EditAlarm,
            "deletealarm" => AlertChangeAction.DeleteAlarm,
            "enablealarm" => AlertChangeAction.EnableAlarm,
            "disablealarm" => AlertChangeAction.DisableAlarm,
            "notificationsettings" => AlertChangeAction.NotificationSettings,
            _ => AlertChangeAction.Unclear,
        };

        var ruleId = response.RuleId?.Trim();
        if (ruleId is not null && !AlertRuleCatalogue.IsKnown(ruleId))
            ruleId = null;

        var alarmLabel = snapshot.FindAlarm(response.AlarmLabel)?.Label;

        return new AlertChangePlan
        {
            Action = action,
            RuleId = ruleId,
            AlarmLabel = alarmLabel,
            Name = string.IsNullOrWhiteSpace(response.Name) ? null : response.Name.Trim(),
            Metric = ParseEnum<AlarmMetric>(response.Metric),
            Statistic = ParseEnum<AlarmStatistic>(response.Statistic),
            Operator = Canonical(response.Operator) switch
            {
                "above" or "greaterthan" or "over" => AlarmOperator.GreaterThan,
                "atorabove" or "greaterthanorequalto" or "reaches" => AlarmOperator.GreaterThanOrEqualTo,
                "below" or "lessthan" or "under" => AlarmOperator.LessThan,
                "atorbelow" or "lessthanorequalto" or "dropsto" => AlarmOperator.LessThanOrEqualTo,
                _ => null,
            },
            ThresholdKind = Canonical(response.ThresholdKind) switch
            {
                "level" or "absolute" => AlarmThresholdKind.Absolute,
                "percentofusual" or "baselinepercent" or "percent" => AlarmThresholdKind.BaselinePercent,
                "departurefromusual" or "baselinesigma" or "sigma" => AlarmThresholdKind.BaselineSigma,
                _ => null,
            },
            ThresholdValue = response.ThresholdValue,
            PeriodMinutes = response.PeriodMinutes is > 0 ? response.PeriodMinutes : null,
            EvaluationPeriods = response.EvaluationPeriods is > 0 ? response.EvaluationPeriods : null,
            DatapointsToAlarm = response.DatapointsToAlarm is > 0 ? response.DatapointsToAlarm : null,
            Severity = Canonical(response.Severity) switch
            {
                "yellow" => AlertSeverity.Yellow,
                "orange" => AlertSeverity.Orange,
                "red" => AlertSeverity.Red,
                _ => null,
            },
            ContextGate = response.WhileStill switch
            {
                true => AlarmContextGate.Inactive,
                false => AlarmContextGate.None,
                null => null,
            },
        };
    }

    /// <summary>
    /// A label that is one of the enum's names, case-insensitively, or null. Matched against the
    /// names rather than handed to <c>TryParse</c>, which accepts "1" as the first member — a
    /// numeric string is not a label this prompt offered, and must not become a reading.
    /// </summary>
    private static T? ParseEnum<T>(string? label) where T : struct, Enum
    {
        var wanted = label?.Trim();
        if (string.IsNullOrEmpty(wanted))
            return null;

        var name = Enum.GetNames<T>().FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : Enum.Parse<T>(name);
    }

    private static string? Canonical(string? label) =>
        string.IsNullOrWhiteSpace(label)
            ? null
            : new string(label.Where(char.IsLetter).ToArray()).ToLowerInvariant();

    private static string Number(decimal value) =>
        value == decimal.Truncate(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <remarks>
    /// Every field but the action is optional and described, for the lesson
    /// <see cref="DataQueryPlannerService.DataQueryPlanAiResponse"/> records: the descriptions are
    /// copied into the schema the model is constrained by, and are the only account of what
    /// belongs in each field.
    /// </remarks>
    internal sealed record AlertChangeAiResponse
    {
        [Description("What the caregiver is asking for, exactly one of the actions listed.")]
        public required string Action { get; init; }

        [Description("For enableRule or disableRule, the id of the app's own alert, exactly as listed.")]
        public string? RuleId { get; init; }

        [Description("For editAlarm, deleteAlarm, enableAlarm or disableAlarm, the label of the existing alarm, exactly as listed.")]
        public string? AlarmLabel { get; init; }

        [Description("The reading an alarm watches, exactly as listed; omitted when the caregiver named none.")]
        public string? Metric { get; init; }

        [Description("Which side of the level alerts: above, atOrAbove, below, or atOrBelow.")]
        public string? Operator { get; init; }

        [Description("The level as a number in the reading's unit, or the percent of their usual.")]
        public decimal? ThresholdValue { get; init; }

        [Description("level, percentOfUsual, or departureFromUsual; omitted for a plain level.")]
        public string? ThresholdKind { get; init; }

        [Description("Average, Minimum, Maximum, Sum or Latest, only when the caregiver said so.")]
        public string? Statistic { get; init; }

        [Description("The window in minutes, only when the caregiver said so.")]
        public int? PeriodMinutes { get; init; }

        [Description("How many readings to look at, only when the caregiver said so.")]
        public int? EvaluationPeriods { get; init; }

        [Description("How many of those must cross the level, only when the caregiver said so.")]
        public int? DatapointsToAlarm { get; init; }

        [Description("yellow, orange or red, only when the caregiver said how urgent.")]
        public string? Severity { get; init; }

        [Description("True when the alarm should only count while the person is still; omitted when not said.")]
        public bool? WhileStill { get; init; }

        [Description("A name the caregiver gave the alarm, only when they gave one.")]
        public string? Name { get; init; }
    }
}
