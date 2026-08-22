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
        AddLastNight(lines, baseline, today);
        AddDay(lines, baseline, yesterday, complete: true, localNow, "Yesterday");
        AddDay(lines, baseline, today, complete: false, localNow, "Today so far");

        if (lines.Count == 0)
            return string.Empty;

        return $"""

            --- Computed observations ---
            {string.Join("\n", lines)}
            """ + "\n";
    }

    /// <summary>
    /// Last night against this member's own usual, when it was far enough off to be worth saying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A computed observation rather than a line in the usual-pattern block, which is where this
    /// lived. The prompt tells the model to lead with what is in here and not to recap every listed
    /// figure, so a finding outside it competes with one inside it and loses — measured on a member
    /// whose sleep card read 2.9 hours against a usual of about seven, whose summaries led with
    /// heart rate and never mentioned the night at all. Sleep is not a lesser signal than a resting
    /// heart rate a few beats up, and the block the model is told to lead with is where the things
    /// worth leading with belong.
    /// </para>
    /// <para>
    /// Judged by <see cref="StatisticalAlertRules.IrregularSleep"/>'s own threshold, for the reason
    /// the rest of this file reuses those thresholds: a summary must not soothe over a night the
    /// alert engine pages about.
    /// </para>
    /// <para>
    /// Read off <paramref name="today"/> because a sleep session is attributed to the civil day it
    /// ended on — last night is today's row, not yesterday's. It is stated once, on its own line,
    /// rather than per-day: "today so far" has no night in it yet, and the night before last is not
    /// something a caregiver is being asked to act on this morning.
    /// </para>
    /// </remarks>
    private static void AddLastNight(List<string> lines, PatternBaseline baseline, ActivityLog? today)
    {
        if (baseline.AvgSleepMinutes is not > 0 || today?.SleepMinutes is not { } lastNight)
            return;

        var usual = baseline.AvgSleepMinutes.Value;
        if (Math.Abs(lastNight - usual) <= usual * StatisticalAlertRules.DeviationFraction)
            return;

        var direction = lastNight < usual ? "well short of" : "well past";
        lines.Add(
            $"- Last night: {Hours(lastNight)} hours of sleep (usual {Hours(usual)}) — {direction} their usual.");
    }

    /// <summary>
    /// Minutes as hours to one decimal, always invariant: the prompt is model input and a cacheable
    /// fixed-prefix construction, so nothing in it may vary with the host's ambient culture.
    /// </summary>
    private static string Hours(int minutes) =>
        (minutes / 60.0).ToString("F1", CultureInfo.InvariantCulture);

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
    /// Readings that sit outside this member's usual or the published adult band, named for the
    /// prompt — above for heart rate and breathing, below for oxygen and heart rate variability.
    /// Empty when nothing is off.
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

        // Overnight HRV below the member's own usual by the margin the alert rule uses. Phrased as
        // "lower than usual" rather than as a raised vital, because it is the one reading here
        // where the worrying direction is down — a summary that listed it among things "running
        // high" would read as the opposite of what it is.
        if (baseline.AvgHeartRateVariabilityMs is > 0 and { } usualHrv
            && log.HeartRateVariabilityMs is { } hrv
            && hrv < usualHrv - HeartRateVariabilityMarginMs(baseline))
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"heart rate variability {hrv:0.#} ms overnight, lower than their usual {usualHrv:0.#} ms"));
        }

        // The overnight figure, not the daily one — see StatisticalAlertRules.OvernightBreathingUp
        // for why a whole-day average moves for reasons that are not about health.
        if (StatisticalAlertRules.OvernightBreathingUp(baseline, log) is not null
            && log.OvernightBreathingRate is { } overnightBreathing
            && baseline.AvgOvernightBreathingRate is { } usualBreathing)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"breathing {overnightBreathing:0.#} a minute asleep, above their usual {usualBreathing:0.#}"));
        }

        // Named here even though the still-day pairing below would also catch it, because the
        // pairing reads "quiet day" from steps alone: a heart that worked while the steps stayed
        // low is the case where a quiet day is not a restful one.
        if (StatisticalAlertRules.ElevatedZoneWithoutMovement(baseline, log) is not null
            && BaselineCalculator.ElevatedZoneMinutes(log) is { } elevated)
        {
            parts.Add($"{elevated} minutes with their heart rate raised, on a day of little movement");
        }

        return parts;
    }

    /// <summary>
    /// The HRV margin the drop rule uses, so the digest and the alert engine agree on what counts
    /// as low — the same reason every other threshold in this file is borrowed rather than chosen.
    /// </summary>
    private static decimal HeartRateVariabilityMarginMs(PatternBaseline baseline) =>
        Math.Max(
            StatisticalAlertRules.HrvSigmaMultiplier * (baseline.StdDevHeartRateVariability ?? 0m),
            (baseline.AvgHeartRateVariabilityMs ?? 0m) * StatisticalAlertRules.HrvMarginFloorFraction);

    private static double HeartRateMarginBpm(PatternBaseline baseline) =>
        Math.Max(
            StatisticalAlertRules.HrSigmaMultiplier * (double)(baseline.StdDevHeartRate ?? 0),
            StatisticalAlertRules.HrMarginFloorBpm);

    private static string StepsAgainstUsual(PatternBaseline baseline, ActivityLog log) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{log.Steps:N0} steps (usual {baseline.AvgSteps:N0})");
}
