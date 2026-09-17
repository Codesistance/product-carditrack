using System.Globalization;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Services;

/// <summary>
/// One comparable string for everything a save would overwrite on an alarm — what a change
/// proposed from the effective list carries, and what the alarm service compares against the
/// row it is about to write, so a proposal written from a stale picture is refused rather than
/// applied over someone else's change.
/// </summary>
public static class MetricAlarmFingerprint
{
    public static string Of(MetricAlarmResponse row)
    {
        ArgumentNullException.ThrowIfNull(row);
        // Invariant culture throughout: the fingerprint is written on one request and compared
        // on another, possibly on another instance, and "0.5" must not become "0,5" in between.
        return string.Join("|",
            row.Name, row.Metric, row.Statistic, row.Operator, row.ThresholdKind,
            row.ThresholdValue.ToString(CultureInfo.InvariantCulture),
            row.PeriodMinutes.ToString(CultureInfo.InvariantCulture),
            row.EvaluationPeriods.ToString(CultureInfo.InvariantCulture),
            row.DatapointsToAlarm.ToString(CultureInfo.InvariantCulture),
            row.MissingDataTreatment, row.Severity, row.ContextGate, row.IsEnabled, row.Provenance);
    }
}
