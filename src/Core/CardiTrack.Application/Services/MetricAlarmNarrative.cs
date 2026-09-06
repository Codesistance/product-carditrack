using System.Globalization;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Turns an alarm into the sentences a caregiver reads — on the settings card, and in the alert it
/// raises. One place rather than two, so the alarm a caregiver reviews in settings is described in
/// the same words as the alert it later produces.
/// <para>
/// <b>These sentences name an observation, never a condition.</b> "Resting heart rate above the
/// level you set", not "tachycardia". A wellness product that phrases a threshold as a diagnosis is
/// making a medical-device claim out of a string, and the threshold's own provenance — that the
/// caregiver chose it — is both the honest framing and the protective one.
/// </para>
/// </summary>
public static class MetricAlarmNarrative
{
    /// <summary>
    /// The alarm's condition as one sentence: "Average heart rate is above 120 bpm over 10 minutes,
    /// on 2 of 2 readings, while they are still."
    /// </summary>
    public static string Condition(MetricAlarm alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        return Compose(alarm.Metric, alarm.Statistic, alarm.Operator, alarm.ThresholdKind,
            alarm.ThresholdValue, alarm.PeriodMinutes, alarm.EvaluationPeriods,
            alarm.DatapointsToAlarm, alarm.ContextGate);
    }

    /// <summary>
    /// The same sentence for an alarm still being built. The builder previews it as the caregiver
    /// picks, and previewing it any other way would let the screen promise something the saved
    /// alarm does not say.
    /// </summary>
    public static string Condition(SaveMetricAlarmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Compose(request.Metric, request.Statistic, request.Operator, request.ThresholdKind,
            request.ThresholdValue, request.PeriodMinutes, request.EvaluationPeriods,
            request.DatapointsToAlarm, request.ContextGate);
    }

    private static string Compose(
        AlarmMetric metricKind, AlarmStatistic statistic, AlarmOperator comparison,
        AlarmThresholdKind thresholdKind, decimal thresholdValue, int periodMinutes,
        int evaluationPeriods, int datapointsToAlarm, AlarmContextGate contextGate)
    {
        var definition = AlarmMetricCatalogue.Find(metricKind);
        var metric = definition?.Title ?? metricKind.ToString();
        var unit = definition?.Unit ?? string.Empty;

        var subject = statistic switch
        {
            AlarmStatistic.Average => $"Average {Lower(metric)}",
            AlarmStatistic.Minimum => $"Lowest {Lower(metric)}",
            AlarmStatistic.Maximum => $"Highest {Lower(metric)}",
            AlarmStatistic.Sum => $"Total {Lower(metric)}",
            _ => metric,
        };

        var comparisonWords = comparison switch
        {
            AlarmOperator.GreaterThan => "is above",
            AlarmOperator.GreaterThanOrEqualTo => "reaches",
            AlarmOperator.LessThan => "is below",
            AlarmOperator.LessThanOrEqualTo => "drops to",
            _ => "reaches",
        };

        var level = thresholdKind switch
        {
            AlarmThresholdKind.BaselinePercent =>
                $"{Number(thresholdValue)}% of their usual",
            AlarmThresholdKind.BaselineSigma =>
                $"{Number(thresholdValue)} × their usual variation "
                + $"{(comparison is AlarmOperator.GreaterThan or AlarmOperator.GreaterThanOrEqualTo ? "above" : "below")} normal",
            _ => string.IsNullOrEmpty(unit)
                ? Number(thresholdValue)
                : $"{Number(thresholdValue)} {unit}",
        };

        var window = Window(periodMinutes);
        var count = evaluationPeriods > 1
            ? $", on {datapointsToAlarm} of the last {evaluationPeriods}"
            : string.Empty;
        var gate = contextGate == AlarmContextGate.Inactive ? ", while they are still" : string.Empty;

        return $"{subject} {comparisonWords} {level} {window}{count}{gate}.";
    }

    /// <summary>The body of the alert a firing alarm raises.</summary>
    public static string AlertMessage(MetricAlarm alarm, AlarmVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentNullException.ThrowIfNull(verdict);

        var definition = AlarmMetricCatalogue.Find(alarm.Metric);
        var metric = Lower(definition?.Title ?? alarm.Metric.ToString());
        var unit = definition?.Unit is { Length: > 0 } u ? $" {u}" : string.Empty;

        var observed = verdict.ObservedValue is { } value
            ? $"{Number((decimal)value)}{unit}"
            : "a reading";

        var threshold = verdict.EffectiveThreshold is { } limit
            ? $"{Number(limit)}{unit}"
            : "the level you set";

        var direction = alarm.Operator is AlarmOperator.GreaterThan or AlarmOperator.GreaterThanOrEqualTo
            ? "above"
            : "below";

        var evidence = alarm.EvaluationPeriods > 1
            ? $" This held on {verdict.BreachingDatapoints} of the last {alarm.EvaluationPeriods} readings."
            : string.Empty;

        // Deliberately closes on what was watched and who set it, not on what it might mean. The
        // number and its provenance are the whole of what CardiTrack knows.
        return $"Their {metric} was {observed}, {direction} the {threshold} you asked to be told "
            + $"about.{evidence} Worth a check-in.";
    }

    /// <summary>The alert headline — the caregiver's own label for the alarm, which is the thing
    /// they will recognise on a lock screen at two in the morning.</summary>
    public static string AlertTitle(MetricAlarm alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        return string.IsNullOrWhiteSpace(alarm.Name) ? "An alarm you set has been reached" : alarm.Name;
    }

    private static string Window(int periodMinutes) => periodMinutes switch
    {
        AlarmMetricCatalogue.DailyPeriodMinutes => "on a day",
        60 => "over an hour",
        1 => "in a minute",
        var m when m % 60 == 0 => $"over {m / 60} hours",
        var m => $"over {m} minutes",
    };

    private static string Lower(string title) =>
        title.Length > 0 && char.IsUpper(title[0]) && !title.All(char.IsUpper)
            ? char.ToLowerInvariant(title[0]) + title[1..]
            : title;

    private static string Number(decimal value) =>
        value == decimal.Truncate(value)
            ? ((long)value).ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
}
