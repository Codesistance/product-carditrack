using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
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
    /// Vitals that sit above (or, for oxygen, below) this member's usual / the published
    /// adult band, named for the prompt. Empty when nothing is off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A finding names where the reading landed against the published adult band as well as against
    /// the member's own usual, because the two answer different questions and a summary that gives
    /// only the first can mislead in both directions. "84 bpm (usual 62)" reads as an event whether
    /// 84 is an ordinary adult resting rate or not; and a member whose usual is 98 produces no
    /// finding at all on a day they read 98, because nothing departed from anything — the reading is
    /// outside the range the AHA publishes every day of the week, and on the strength of their own
    /// baseline alone the digest would never say so.
    /// </para>
    /// <para>
    /// That last case is the one <see cref="AtUsualButOutsideBand"/> exists for, and it is
    /// deliberately one-sided: only a rate <em>above</em> the band earns a line at their own usual.
    /// The AHA floor of 60 is a great many adults' settled normal — anyone fit, anyone on a
    /// rate-controlling medication — and a digest that told those families every single day that
    /// their person's heart rate is below the typical range would be a digest they stop reading, at
    /// the cost of the day it says something else. A rate that sits above the band is both rarer as
    /// a settled state and more consistently worth a mention.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> RaisedVitals(PatternBaseline baseline, ActivityLog log)
    {
        var parts = new List<string>();
        var usualResting = baseline.AvgRestingHeartRate;
        var margin = HeartRateMarginBpm(baseline);
        var typicalResting = HealthReferenceRanges.RestingHeartRate;

        if (StatisticalAlertRules.ElevatedHeartRate(baseline, log) is not null
            && log.RestingHeartRate is { } resting
            && usualResting is { } usual)
        {
            parts.Add($"resting heart rate {resting} bpm (usual {usual}), "
                + HealthReferenceRanges.BandPlacement(typicalResting, resting, "bpm"));
        }
        else if (AtUsualButOutsideBand(typicalResting, log.RestingHeartRate))
        {
            // No departure to report — this is what their heart rate does — so the finding is the
            // absolute one, and it says outright that nothing changed today. Without that clause a
            // model handed a lone out-of-band figure writes it up as news.
            parts.Add($"resting heart rate {log.RestingHeartRate} bpm — close to their usual, but "
                + $"{HealthReferenceRanges.BandClause(typicalResting, log.RestingHeartRate, "bpm")}");
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

    /// <summary>
    /// A resting heart rate that broke no pattern but sits above the published adult band — see the
    /// remarks on <see cref="RaisedVitals"/> for why only that direction counts.
    /// </summary>
    private static bool AtUsualButOutsideBand(MetricReference typical, int? restingHeartRate) =>
        HealthReferenceRanges.Position(typical, restingHeartRate) == BandPosition.Above;

    private static double HeartRateMarginBpm(PatternBaseline baseline) =>
        Math.Max(
            StatisticalAlertRules.HrSigmaMultiplier * (double)(baseline.StdDevHeartRate ?? 0),
            StatisticalAlertRules.HrMarginFloorBpm);

    private static string StepsAgainstUsual(PatternBaseline baseline, ActivityLog log) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{log.Steps:N0} steps (usual {baseline.AvgSteps:N0})");
}
