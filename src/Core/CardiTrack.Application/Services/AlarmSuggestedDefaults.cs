using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Fills in what a caregiver left unsaid when they ask for an alarm in a sentence — "alert me
/// if his heart rate goes over 120" names a reading, a direction and a level, and the builder
/// asks for nine things. The defaults are the alarm catalogue's suggested shapes
/// (<c>docs/technical/alarm_catalogue.md</c> §2): a short window judged on more than one
/// reading, and the stillness gate on heart rate, because 120 bpm on a staircase is a heart doing
/// its job. What cannot be defaulted — the reading, the direction, the level — is named as
/// missing so the reply can ask for exactly that.
/// </summary>
public static class AlarmSuggestedDefaults
{
    /// <summary>The window a sub-daily alarm watches when the caregiver named none.</summary>
    public const int DefaultSubDailyPeriodMinutes = 5;

    /// <summary>
    /// A new alarm from a plan, or null with <paramref name="missing"/> naming what the plan
    /// lacked. Validated by the caller, not here: this only decides values, and
    /// <see cref="MetricAlarmValidation"/> decides whether they are legal.
    /// </summary>
    public static SaveMetricAlarmRequest? Build(AlertChangePlan plan, out IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var gaps = new List<string>();

        var definition = plan.Metric is { } metric ? AlarmMetricCatalogue.Find(metric) : null;
        if (definition is null)
            gaps.Add("which reading to watch");
        if (plan.Operator is null)
            gaps.Add("whether to alert above or below the level");
        if (plan.ThresholdValue is null)
            gaps.Add("the level");

        missing = gaps;
        if (definition is null || plan.Operator is null || plan.ThresholdValue is null)
            return null;

        var request = new SaveMetricAlarmRequest
        {
            Metric = definition.Metric,
            Operator = plan.Operator.Value,
            ThresholdKind = plan.ThresholdKind ?? AlarmThresholdKind.Absolute,
            ThresholdValue = plan.ThresholdValue.Value,
            Statistic = plan.Statistic ?? DefaultStatistic(definition),
            // A window the caregiver named is kept as named, legal or not: validation refuses it
            // in the builder's words, where a silent swap to the default would propose a
            // different alarm from the one they asked for.
            PeriodMinutes = plan.PeriodMinutes ?? DefaultPeriod(definition),
            EvaluationPeriods = plan.EvaluationPeriods ?? DefaultEvaluationPeriods(definition),
            // Never more than the readings looked at: a caregiver who asked for one reading and
            // said nothing about the count meant one of one, not the suggested two of one.
            DatapointsToAlarm = plan.DatapointsToAlarm
                ?? Math.Min(DefaultDatapoints(definition), plan.EvaluationPeriods ?? DefaultEvaluationPeriods(definition)),
            ContextGate = plan.ContextGate ?? DefaultGate(definition),
            Severity = plan.Severity ?? AlertSeverity.Yellow,
            MissingDataTreatment = AlarmMissingDataTreatment.Missing,
            IsEnabled = true,
        };

        // A caregiver who named the count without the range meant "on N readings", not N of one.
        if (plan.DatapointsToAlarm is { } wanted && plan.EvaluationPeriods is null && wanted > request.EvaluationPeriods)
            request.EvaluationPeriods = wanted;

        request.Name = NameFor(plan.Name, request, definition);
        request.ConfirmCriticalSeverity = request.Severity == AlertSeverity.Red;
        return request;
    }

    /// <summary>
    /// An existing alarm with the plan's changes laid over it — the fields the caregiver named
    /// change, everything else stays as the row has it. A change of reading starts the alarm's
    /// shape over, since a statistic or window legal on one reading is not on another.
    /// </summary>
    public static SaveMetricAlarmRequest? Revise(
        MetricAlarmResponse row, AlertChangePlan plan, out IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Metric is { } metric && metric != row.Metric)
        {
            var rebuilt = Build(plan with
            {
                Operator = plan.Operator ?? row.Operator,
                Severity = plan.Severity ?? row.Severity,
                Name = plan.Name ?? row.Name,
            }, out missing);
            if (rebuilt is not null)
                rebuilt.IsEnabled = row.IsEnabled;
            return rebuilt;
        }

        missing = [];
        var request = new SaveMetricAlarmRequest
        {
            Name = string.IsNullOrWhiteSpace(plan.Name) ? row.Name : plan.Name.Trim(),
            Metric = row.Metric,
            Statistic = plan.Statistic ?? row.Statistic,
            Operator = plan.Operator ?? row.Operator,
            ThresholdKind = plan.ThresholdKind ?? row.ThresholdKind,
            ThresholdValue = plan.ThresholdValue ?? row.ThresholdValue,
            PeriodMinutes = plan.PeriodMinutes ?? row.PeriodMinutes,
            EvaluationPeriods = plan.EvaluationPeriods ?? row.EvaluationPeriods,
            DatapointsToAlarm = plan.DatapointsToAlarm
                ?? Math.Min(row.DatapointsToAlarm, plan.EvaluationPeriods ?? row.EvaluationPeriods),
            MissingDataTreatment = row.MissingDataTreatment,
            Severity = plan.Severity ?? row.Severity,
            ContextGate = plan.ContextGate ?? row.ContextGate,
            IsEnabled = row.IsEnabled,
        };

        if (plan.DatapointsToAlarm is { } wanted && plan.EvaluationPeriods is null && wanted > request.EvaluationPeriods)
            request.EvaluationPeriods = wanted;

        request.ConfirmCriticalSeverity = request.Severity == AlertSeverity.Red;
        return request;
    }

    /// <summary>The row as a request with only the switch changed — what the list page's toggle sends.</summary>
    public static SaveMetricAlarmRequest Switched(MetricAlarmResponse row, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new SaveMetricAlarmRequest
        {
            Name = row.Name,
            Metric = row.Metric,
            Statistic = row.Statistic,
            Operator = row.Operator,
            ThresholdKind = row.ThresholdKind,
            ThresholdValue = row.ThresholdValue,
            PeriodMinutes = row.PeriodMinutes,
            EvaluationPeriods = row.EvaluationPeriods,
            DatapointsToAlarm = row.DatapointsToAlarm,
            MissingDataTreatment = row.MissingDataTreatment,
            Severity = row.Severity,
            ContextGate = row.ContextGate,
            IsEnabled = enabled,
            ConfirmCriticalSeverity = row.Severity == AlertSeverity.Red,
        };
    }

    private static AlarmStatistic DefaultStatistic(AlarmMetricDefinition definition) =>
        definition.Source == AlarmMetricSource.Daily
            ? AlarmStatistic.Latest
            : definition.Statistics.Contains(AlarmStatistic.Average) ? AlarmStatistic.Average : AlarmStatistic.Sum;

    private static int DefaultPeriod(AlarmMetricDefinition definition) =>
        definition.Source == AlarmMetricSource.Daily
            ? AlarmMetricCatalogue.DailyPeriodMinutes
            : DefaultSubDailyPeriodMinutes;

    // The catalogue's suggested shapes: two of two short windows, two of the last three days.
    private static int DefaultEvaluationPeriods(AlarmMetricDefinition definition) =>
        definition.Source == AlarmMetricSource.Daily ? 3 : 2;

    private static int DefaultDatapoints(AlarmMetricDefinition definition) =>
        definition.Source == AlarmMetricSource.Daily ? 2 : 2;

    private static AlarmContextGate DefaultGate(AlarmMetricDefinition definition) =>
        definition.Metric == AlarmMetric.HeartRate ? AlarmContextGate.Inactive : AlarmContextGate.None;

    /// <summary>The caregiver's name for it, as given, or one built from what it watches — "Heart
    /// rate above 120 bpm". A given name is never shortened here: an overlong one is refused by
    /// validation in the builder's words rather than saved as something they did not say.</summary>
    private static string NameFor(string? given, SaveMetricAlarmRequest request, AlarmMetricDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(given))
            return given.Trim();

        var direction = request.Operator is AlarmOperator.GreaterThan or AlarmOperator.GreaterThanOrEqualTo
            ? "above"
            : "below";
        var level = request.ThresholdKind switch
        {
            AlarmThresholdKind.BaselinePercent => $"{request.ThresholdValue:0.#}% of usual",
            AlarmThresholdKind.BaselineSigma => "usual",
            _ => string.IsNullOrEmpty(definition.Unit)
                ? $"{request.ThresholdValue:0.#}"
                : $"{request.ThresholdValue:0.#} {definition.Unit}",
        };

        var name = $"{definition.Title} {direction} {level}";
        return name.Length <= MetricAlarmValidation.MaxNameLength ? name : name[..MetricAlarmValidation.MaxNameLength];
    }
}
