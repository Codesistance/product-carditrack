using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// One evaluation period's contribution. <see cref="Value"/> null means the period reported
/// nothing, which is what <see cref="AlarmMissingDataTreatment"/> decides the meaning of.
/// <see cref="GateSatisfied"/> false means the period was measured and positively failed the
/// alarm's context gate — the member was moving when the alarm only watches stillness. That is
/// not missing data: we know what happened and it does not count, so the period is non-breaching
/// rather than absent.
/// </summary>
public readonly record struct AlarmDatapoint(double? Value, bool GateSatisfied = true);

/// <summary>What one tick concluded about one alarm.</summary>
/// <param name="RaisedNow">
/// True only on the transition into <see cref="AlarmEvaluationState.Alarm"/> while the alarm is
/// armed — see the <c>armed</c> parameter of <see cref="MetricAlarmEvaluator.Evaluate"/>. This,
/// not the state itself, is what writes an alert.
/// </param>
public sealed record AlarmVerdict(
    AlarmEvaluationState State,
    bool RaisedNow,
    decimal? EffectiveThreshold,
    double? ObservedValue,
    int BreachingDatapoints,
    int EvaluatedDatapoints);

/// <summary>
/// The user-defined alarm engine: a pure function from an alarm, a window of datapoints and the
/// member's baseline to a verdict. Deliberately free of I/O, like <see cref="StatisticalAlertRules"/>,
/// so every threshold and every boundary is unit-testable without a host or a database.
/// <para>
/// It answers three questions in order. What is the threshold actually worth for this member
/// (absolute, or resolved against their own baseline)? Do enough of the datapoints in the window
/// breach it? And — the question that needs the previous state — is this tick the moment the alarm
/// <em>entered</em> that condition, or has it been sitting there since the last tick?
/// </para>
/// </summary>
public static class MetricAlarmEvaluator
{
    /// <summary>
    /// How far inside the firing threshold a value must come back before a standing alarm clears,
    /// as a fraction of the threshold's own magnitude. Without it a heart rate hovering on 120
    /// crosses back and forth all afternoon and every crossing is a fresh page — the flapping that
    /// makes people turn alarms off. Five percent is engineering judgement; the need for hysteresis
    /// is not, and is the same reason cloud alarms and clinical monitors both damp their thresholds.
    /// </summary>
    public const decimal HysteresisFraction = 0.05m;

    /// <param name="armed">
    /// Whether an alert may be written this tick. False while the current episode's alert is still
    /// outstanding — the alarm has fired and has not yet returned to <see cref="AlarmEvaluationState.Ok"/>.
    /// That is what keeps a standing episode that dips through
    /// <see cref="AlarmEvaluationState.InsufficientData"/> (the watch off for a quarter of an hour)
    /// from paging a second time when the readings come back: the state left Alarm, but the
    /// episode did not end. Only a return to normal re-arms.
    /// </param>
    public static AlarmVerdict Evaluate(
        MetricAlarm alarm,
        IReadOnlyList<AlarmDatapoint> datapoints,
        PatternBaseline? baseline,
        AlarmEvaluationState previousState,
        bool armed = true)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentNullException.ThrowIfNull(datapoints);

        var threshold = ResolveThreshold(alarm, baseline);
        if (threshold is null)
        {
            // A baseline-relative alarm on a member who has no established baseline for this
            // metric. Silence is the only safe answer: there is no number to compare against, and
            // inventing a population figure would be exactly the tailoring-under-someone-else's-name
            // this product refuses everywhere else.
            return new AlarmVerdict(AlarmEvaluationState.InsufficientData, false, null, null, 0, 0);
        }

        // While the alarm is standing, judge against a threshold pulled slightly toward the safe
        // side, so a value has to come properly back before the alarm clears.
        var effective = previousState == AlarmEvaluationState.Alarm
            ? Relax(threshold.Value, alarm.Operator)
            : threshold.Value;

        var breaching = 0;
        var measured = 0;
        var missing = 0;
        double? observed = null;

        foreach (var point in datapoints)
        {
            if (point.Value is not { } value)
            {
                missing++;
                continue;
            }

            measured++;
            observed = value;

            // A period we measured and that failed the context gate is a period we know did not
            // count. It is present, and it is not a breach.
            if (point.GateSatisfied && Breaches(value, effective, alarm.Operator))
                breaching++;
        }

        var state = Decide(alarm, previousState, breaching, measured, missing, datapoints.Count);

        return new AlarmVerdict(
            state,
            RaisedNow: armed && state == AlarmEvaluationState.Alarm && previousState != AlarmEvaluationState.Alarm,
            EffectiveThreshold: threshold,
            ObservedValue: observed,
            BreachingDatapoints: breaching,
            EvaluatedDatapoints: measured);
    }

    private static AlarmEvaluationState Decide(
        MetricAlarm alarm, AlarmEvaluationState previousState,
        int breaching, int measured, int missing, int total)
    {
        switch (alarm.MissingDataTreatment)
        {
            case AlarmMissingDataTreatment.NotBreaching:
                // Absence counts as within the threshold, so the window is always judgeable.
                return breaching >= alarm.DatapointsToAlarm
                    ? AlarmEvaluationState.Alarm
                    : AlarmEvaluationState.Ok;

            case AlarmMissingDataTreatment.Ignore:
                // Missing periods are dropped. A window with nothing left in it holds whatever the
                // alarm was already in — which is the point of this option: a wearer who takes the
                // watch off mid-episode does not thereby resolve the episode.
                if (measured == 0)
                    return previousState;
                return breaching >= alarm.DatapointsToAlarm
                    ? AlarmEvaluationState.Alarm
                    : AlarmEvaluationState.Ok;

            case AlarmMissingDataTreatment.Missing:
            default:
                // CloudWatch's default, and ours. A window with no readings at all cannot be
                // judged either way, and says so rather than guessing in either direction.
                if (total == 0 || missing == total)
                    return AlarmEvaluationState.InsufficientData;
                return breaching >= alarm.DatapointsToAlarm
                    ? AlarmEvaluationState.Alarm
                    : AlarmEvaluationState.Ok;
        }
    }

    /// <summary>
    /// What the alarm's stored number is actually worth for this member. Null where a
    /// baseline-relative alarm has no baseline figure to stand on.
    /// </summary>
    public static decimal? ResolveThreshold(MetricAlarm alarm, PatternBaseline? baseline)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        if (alarm.ThresholdKind == AlarmThresholdKind.Absolute)
            return alarm.ThresholdValue;

        if (baseline is null)
            return null;

        var average = AlarmMetricCatalogue.BaselineAverage(alarm.Metric, baseline);
        if (average is not { } mean)
            return null;

        switch (alarm.ThresholdKind)
        {
            case AlarmThresholdKind.BaselinePercent:
                return mean * (alarm.ThresholdValue / 100m);

            case AlarmThresholdKind.BaselineSigma:
                var sigma = AlarmMetricCatalogue.BaselineStdDev(alarm.Metric, baseline);
                if (sigma is not { } deviation)
                    return null;
                // The operator carries the sign: watching for a value above their usual puts the
                // threshold above the mean, watching for one below puts it below. Storing a signed
                // number instead would let an alarm be built that reads "alert when the heart rate
                // is above two sigma below the usual", which is a sentence nobody means.
                return IsUpward(alarm.Operator)
                    ? mean + alarm.ThresholdValue * deviation
                    : mean - alarm.ThresholdValue * deviation;

            default:
                return alarm.ThresholdValue;
        }
    }

    private static bool IsUpward(AlarmOperator op) =>
        op is AlarmOperator.GreaterThan or AlarmOperator.GreaterThanOrEqualTo;

    private static bool Breaches(double value, decimal threshold, AlarmOperator op)
    {
        var limit = (double)threshold;
        return op switch
        {
            AlarmOperator.GreaterThan => value > limit,
            AlarmOperator.GreaterThanOrEqualTo => value >= limit,
            AlarmOperator.LessThan => value < limit,
            AlarmOperator.LessThanOrEqualTo => value <= limit,
            _ => false,
        };
    }

    /// <summary>
    /// The threshold a standing alarm is held against — moved toward the safe side by
    /// <see cref="HysteresisFraction"/> of its own magnitude, so clearing needs a real recovery
    /// rather than a rounding error. A threshold of zero has no magnitude to damp and is returned
    /// unchanged.
    /// </summary>
    private static decimal Relax(decimal threshold, AlarmOperator op)
    {
        var band = Math.Abs(threshold) * HysteresisFraction;
        return IsUpward(op) ? threshold - band : threshold + band;
    }
}
