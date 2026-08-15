using System.Globalization;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// Computed findings the family digest is handed so MedGemma interprets the day rather than
/// reciting it. Same division of labour as the rest of the pipeline: .NET does the arithmetic,
/// the model only phrases. Thresholds reuse <see cref="StatisticalAlertRules"/> so a summary
/// cannot soothe over a day the alert engine pages about.
/// <para>
/// The load-bearing pairing is a <em>still day with a raised vital</em> — little movement
/// alongside a heart rate, breathing rate or oxygen reading that would be unsurprising after
/// a walk, and is a warning sign without one. One-sided findings (quiet steps, or a raised
/// vital on a normal-activity day) are still emitted, so the model is not left to invent the
/// comparison.
/// </para>
/// </summary>
public static class DigestInterpretationSignals
{
    /// <summary>
    /// Local hour from which today's in-progress step total is fair to read as a quiet day.
    /// Before this, a low total is a morning, not stillness — the same trap
    /// <see cref="DigestRefreshRules.ReadingsDivergeFromBaseline"/> documents for steps-decline.
    /// </summary>
    public const int TodayStepsComparableFromHour = 16;

    /// <summary>
    /// Local hour used when the member has no typical wake time, so a measured-zero step
    /// count can still be called quiet. Earlier than
    /// <see cref="TodayStepsComparableFromHour"/> because a true zero after mid-morning is
    /// already the no-movement finding, not a running total that might catch up.
    /// </summary>
    public const int MeasuredZeroQuietFromHour = 10;

    /// <summary>
    /// Prompt section, or empty when there is no baseline or nothing off the usual. Empty
    /// rather than a section saying nothing: on a calm member the words are not in the prompt
    /// to be echoed.
    /// </summary>
    public static string Section(
        PatternBaseline? baseline,
        ActivityLog? today,
        ActivityLog? yesterday,
        DateTime localNow)
    {
        if (baseline is null)
            return string.Empty;

        var lines = new List<string>();
        AddDay(lines, baseline, yesterday, complete: true, localNow, "Yesterday");
        AddDay(lines, baseline, today, complete: false, localNow, "Today so far");

        if (lines.Count == 0)
            return string.Empty;

        return $"""

            --- Computed observations ---
            {string.Join("\n", lines)}
            """ + "\n";
    }

    private static void AddDay(
        List<string> lines,
        PatternBaseline baseline,
        ActivityLog? log,
        bool complete,
        DateTime localNow,
        string label)
    {
        if (log is null)
            return;

        var quiet = IsQuiet(baseline, log, complete, localNow);
        var raised = RaisedVitals(baseline, log);

        if (quiet && raised.Count > 0)
        {
            lines.Add(
                $"- {label}: {string.Join(", ", raised)} with {StepsAgainstUsual(baseline, log)} "
                + "— these findings on a still day, not a day of walking.");
            return;
        }

        if (quiet)
            lines.Add($"- {label}: {StepsAgainstUsual(baseline, log)}.");

        if (raised.Count > 0)
            lines.Add($"- {label}: {string.Join(", ", raised)}.");
    }

    /// <summary>
    /// Steps well below this member's usual, on a day whose total is fair to judge. Null
    /// steps never count: not measured is not the same as not moving.
    /// </summary>
    public static bool IsQuiet(
        PatternBaseline baseline, ActivityLog log, bool complete, DateTime localNow)
    {
        if (log.Steps is not { } steps || baseline.AvgSteps is not > 0)
            return false;

        if (complete)
            return StatisticalAlertRules.ActivityDecline(baseline, log) is not null;

        if (steps == 0)
        {
            if (StatisticalAlertRules.NoMorningActivity(baseline, log, localNow) is not null)
                return true;

            return baseline.TypicalWakeTime is null && localNow.Hour >= MeasuredZeroQuietFromHour;
        }

        return localNow.Hour >= TodayStepsComparableFromHour
            && StatisticalAlertRules.ActivityDecline(baseline, log) is not null;
    }

    /// <summary>
    /// Vitals that sit above (or, for oxygen, below) this member's usual / the published
    /// adult band, named for the prompt. Empty when nothing is off.
    /// </summary>
    public static IReadOnlyList<string> RaisedVitals(PatternBaseline baseline, ActivityLog log)
    {
        var parts = new List<string>();
        var usualResting = baseline.AvgRestingHeartRate;
        var margin = HeartRateMarginBpm(baseline);

        if (StatisticalAlertRules.ElevatedHeartRate(baseline, log) is not null
            && log.RestingHeartRate is { } resting
            && usualResting is { } usual)
        {
            parts.Add($"resting heart rate {resting} bpm (usual {usual})");
        }

        if (usualResting is > 0 && log.AvgHeartRate is { } avg
            && avg > usualResting.Value + margin)
        {
            parts.Add($"average heart rate {avg} bpm (usual resting {usualResting})");
        }

        if (log.SpO2Average is { } spo2 && spo2 < HealthReferenceRanges.SpO2.Low)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"oxygen {spo2:0.#}%"));
        }

        if (log.BreathingRate is { } breathing && breathing > HealthReferenceRanges.BreathingRate.High)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture, $"breathing {breathing:0.#} breaths/min"));
        }

        return parts;
    }

    private static double HeartRateMarginBpm(PatternBaseline baseline) =>
        Math.Max(
            StatisticalAlertRules.HrSigmaMultiplier * (double)(baseline.StdDevHeartRate ?? 0),
            StatisticalAlertRules.HrMarginFloorBpm);

    private static string StepsAgainstUsual(PatternBaseline baseline, ActivityLog log) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{log.Steps:N0} steps (usual {baseline.AvgSteps:N0})");
}
