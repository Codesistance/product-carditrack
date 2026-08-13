using System.Text.Json;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>One rule's verdict: everything the orchestrator needs to write the alert.
/// <see cref="NightOf"/> is set by rules that judge one specific night (the civil day the
/// night ended on) rather than the firing day's data — the orchestrator dedups those per
/// night, because late-arriving data can put the same night in front of the rule on two
/// calendar days.</summary>
public sealed record StatisticalAlertCandidate(
    string Rule, AlertType Type, AlertSeverity Severity, string Title, string Message, string MetricValues,
    DateOnly? NightOf = null);

/// <summary>
/// The R1 statistical alert rules (docs/execution/backend/api/alerts.md taxonomy) — pure
/// functions from baseline + daily readings to an alert candidate or null, deliberately free of
/// I/O so every threshold is unit-testable to its boundary. Thresholds are the hard-coded
/// "medium" sensitivity profile (deviation &gt; 30%); the low/high profiles wait on the
/// unbuilt <c>AlertPreferences</c> table.
/// <para>
/// Every rule takes the <b>established 30-day</b> baseline only — provisional 7/14-day
/// baselines never alert (a statistically thin window would trade the &lt;5% false-positive
/// target for early noise), which the orchestrator enforces by what it fetches.
/// </para>
/// <para>
/// Null readings never fire anything: null means "not measured", and the null-vs-zero
/// discipline holds here exactly as it does in ingestion — a day the device did not measure is
/// not a day the member did nothing.
/// </para>
/// </summary>
public static class StatisticalAlertRules
{
    public const string ActivityDeclineRule = "activity_decline";
    public const string IrregularSleepRule = "irregular_sleep";
    public const string ElevatedHeartRateRule = "elevated_heart_rate";
    public const string NoMorningActivityRule = "no_morning_activity";
    public const string LongTermTrendRule = "long_term_trend";

    /// <summary>Medium sensitivity: a reading more than 30% off its baseline is worth a word.</summary>
    public const double DeviationFraction = 0.30;

    /// <summary>
    /// Elevated resting HR margin: 2σ of the member's own variability, floored at 5 bpm so a
    /// member with an unusually steady heart does not get alerted over ordinary variation.
    /// </summary>
    public const int HrSigmaMultiplier = 2;
    public const int HrMarginFloorBpm = 5;

    /// <summary>Grace after the typical wake time before "no movement yet" means anything —
    /// nobody owes their wearable a step count before the kettle has boiled.</summary>
    public const int MorningGraceHours = 2;

    /// <summary>Long-term trend: ≥5% decline week-over-week, sustained across 4 weeks.</summary>
    public const double WeeklyDeclineFraction = 0.05;
    public const int TrendWeeks = 4;

    /// <summary>Days with a steps reading a week needs before its average means anything.</summary>
    public const int TrendMinDaysPerWeek = 4;

    /// <summary>Yesterday's steps more than 30% below the baseline average.</summary>
    public static StatisticalAlertCandidate? ActivityDecline(PatternBaseline baseline, ActivityLog? yesterday)
    {
        if (baseline.AvgSteps is not > 0 || yesterday?.Steps is not { } steps)
            return null;

        var average = baseline.AvgSteps.Value;
        if (steps >= average * (1 - DeviationFraction))
            return null;

        return new StatisticalAlertCandidate(
            ActivityDeclineRule, AlertType.Inactivity, AlertSeverity.Yellow,
            "Activity was well below the usual",
            $"About {steps:N0} steps yesterday against a usual {average:N0} — a quieter day than normal. "
            + "Worth a gentle check-in.",
            Serialize(new { rule = ActivityDeclineRule, steps, baselineAvgSteps = average }));
    }

    /// <summary>
    /// The most recent night's sleep more than 30% off the baseline average, in either direction.
    /// Sleep sessions are attributed to the civil day they <b>ended</b> on, so last night lives on
    /// <em>today's</em> log — the same row the dashboard's sleep card rates — and the orchestrator
    /// passes the freshest log that carries a sleep reading. The candidate names the night it
    /// judged (<see cref="StatisticalAlertCandidate.NightOf"/>) so one night alerts at most once
    /// however late its data arrived.
    /// </summary>
    public static StatisticalAlertCandidate? IrregularSleep(PatternBaseline baseline, ActivityLog? lastNight)
    {
        if (baseline.AvgSleepMinutes is not > 0 || lastNight?.SleepMinutes is not { } sleep)
            return null;

        var average = baseline.AvgSleepMinutes.Value;
        if (Math.Abs(sleep - average) <= average * DeviationFraction)
            return null;

        var direction = sleep < average ? "less" : "more";
        return new StatisticalAlertCandidate(
            IrregularSleepRule, AlertType.Sleep, AlertSeverity.Yellow,
            "Sleep was well off the usual",
            $"Around {sleep / 60.0:F1} hours of sleep, noticeably {direction} than the usual "
            + $"{average / 60.0:F1} — one night is rarely a worry, but it may be worth mentioning.",
            Serialize(new
            {
                rule = IrregularSleepRule, night = lastNight.Date.ToString("O"),
                sleepMinutes = sleep, baselineAvgSleepMinutes = average,
            }),
            NightOf: lastNight.Date);
    }

    /// <summary>Yesterday's resting heart rate above baseline average + max(2σ, 5 bpm).</summary>
    public static StatisticalAlertCandidate? ElevatedHeartRate(PatternBaseline baseline, ActivityLog? yesterday)
    {
        if (baseline.AvgRestingHeartRate is not > 0 || yesterday?.RestingHeartRate is not { } restingHr)
            return null;

        var average = baseline.AvgRestingHeartRate.Value;
        var margin = Math.Max(
            HrSigmaMultiplier * (double)(baseline.StdDevHeartRate ?? 0), HrMarginFloorBpm);
        if (restingHr <= average + margin)
            return null;

        return new StatisticalAlertCandidate(
            ElevatedHeartRateRule, AlertType.HeartRate, AlertSeverity.Orange,
            "Resting heart rate is running high",
            $"Resting heart rate was {restingHr} bpm yesterday, clearly above the usual "
            + $"{average} bpm. Worth checking in today.",
            Serialize(new
            {
                rule = ElevatedHeartRateRule, restingHeartRate = restingHr,
                baselineAvgRestingHeartRate = average, marginBpm = Math.Round(margin, 1),
            }));
    }

    /// <summary>
    /// The device is syncing today — today's log exists and carries a <b>measured zero</b>
    /// steps — yet the member's typical wake time passed more than the grace period ago.
    /// A null steps value never fires: not measured is not the same as not moving.
    /// </summary>
    public static StatisticalAlertCandidate? NoMorningActivity(
        PatternBaseline baseline, ActivityLog? today, DateTime localNow)
    {
        if (baseline.TypicalWakeTime is not { } wake || today?.Steps is not 0)
            return null;

        var earliest = wake.ToTimeSpan().Add(TimeSpan.FromHours(MorningGraceHours));
        if (localNow.TimeOfDay < earliest)
            return null;

        return new StatisticalAlertCandidate(
            NoMorningActivityRule, AlertType.PatternBreak, AlertSeverity.Red,
            "No movement since waking time",
            $"The device is reporting, but no steps have been recorded today — well past the "
            + $"usual waking time of {wake:HH\\:mm}. Please check in.",
            Serialize(new { rule = NoMorningActivityRule, typicalWakeTime = wake.ToString("HH:mm") }));
    }

    /// <summary>
    /// Weekly step averages declining ≥5% week-over-week for 4 consecutive weeks (ending
    /// yesterday). Each week needs enough measured days for its average to mean anything.
    /// </summary>
    public static StatisticalAlertCandidate? LongTermTrend(
        IReadOnlyDictionary<DateOnly, ActivityLog> logsByDate, DateOnly yesterday)
    {
        var weeklyAverages = new double[TrendWeeks];
        for (var week = 0; week < TrendWeeks; week++)
        {
            var weekEnd = yesterday.AddDays(-7 * week);
            var days = Enumerable.Range(0, 7)
                .Select(offset => logsByDate.GetValueOrDefault(weekEnd.AddDays(-offset))?.Steps)
                .OfType<int>()
                .ToList();
            if (days.Count < TrendMinDaysPerWeek)
                return null;

            weeklyAverages[week] = days.Average();
        }

        // Index 0 is the newest week; every week must sit ≥5% below the one before it.
        for (var week = 0; week < TrendWeeks - 1; week++)
        {
            var older = weeklyAverages[week + 1];
            if (older <= 0 || weeklyAverages[week] > older * (1 - WeeklyDeclineFraction))
                return null;
        }

        var totalDecline = 1 - weeklyAverages[0] / weeklyAverages[^1];
        return new StatisticalAlertCandidate(
            LongTermTrendRule, AlertType.Trend, AlertSeverity.Orange,
            "Activity has been declining for weeks",
            $"Daily steps have fallen steadily for {TrendWeeks} weeks — about "
            + $"{totalDecline:P0} lower than a month ago. A pattern like this is worth a "
            + "conversation, and perhaps a mention to a doctor.",
            Serialize(new
            {
                rule = LongTermTrendRule,
                weeklyAvgSteps = weeklyAverages.Reverse().Select(a => Math.Round(a)).ToArray(),
                declineFraction = Math.Round(totalDecline, 2),
            }));
    }

    private static string Serialize(object metrics) => JsonSerializer.Serialize(metrics);
}
