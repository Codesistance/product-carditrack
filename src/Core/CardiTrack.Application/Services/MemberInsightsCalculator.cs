using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Pure, stateless derivations from a member's activity logs/baseline/alerts/sync state — the Key
/// Metrics cards, the overall clinical severity, and the data-pipeline freshness tier. Extracted
/// out of <see cref="DashboardService"/> so <see cref="CardiMemberService"/> (the CardiMember
/// Detail screen's trend sparklines and severity accent) can call the same logic instead of a
/// second copy drifting from it.
/// </summary>
public static class MemberInsightsCalculator
{
    // Deviation-from-baseline thresholds for per-metric status colouring; consistent with
    // the "medium" alert sensitivity in docs/execution/backend/api/alerts.md.
    private const decimal YellowDeviationPercent = 30m;
    private const decimal OrangeDeviationPercent = 50m;

    public const int SeriesDays = 7;
    private const decimal DefaultStepsGoal = 10000m;

    /// <summary>Stars a Key Metrics card rates a reading out of.</summary>
    public const int QualityScoreMax = 5;

    /// <summary>No data for this long or longer reads as an outright gap, not just "a bit stale".</summary>
    public const int RedStaleHours = 12;

    /// <summary>No data for this long or longer is worth a caregiver's attention, short of a gap.</summary>
    public const int AmberStaleHours = 4;

    /// <summary>
    /// Builds the Key Metrics cards from a member's daily history.
    /// </summary>
    /// <remarks>
    /// Each metric is resolved independently, down the days newest-first, rather than all of them
    /// reading a single "latest row". Ingestion stores the day in progress, so today's row appears
    /// as soon as the provider reports anything at all — and a row carrying steps but not yet a
    /// resting heart rate would blank the cards that were populated a moment ago if they all had
    /// to come from the same day. This is the same coalescing rule <see cref="ActivityLogMerge"/>
    /// applies across a member's devices, applied across days.
    /// </remarks>
    public static DashboardMetrics BuildMetrics(List<ActivityLog> logs, PatternBaseline? baseline, DateOnly today)
    {
        var byDate = logs
            .GroupBy(l => l.Date)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.UpdatedDate ?? l.CreatedDate).First());
        var newestFirst = byDate.OrderByDescending(entry => entry.Key).Select(entry => entry.Value).ToList();

        var latestSteps = LatestWith(newestFirst, l => l.Steps);
        var steps = BuildMetric(
            value: latestSteps?.Steps,
            baselineValue: baseline?.AvgSteps,
            unit: "steps",
            series: BuildSeries(byDate, today, l => l.Steps),
            // Steps accumulate through the day, so a day still in progress has nothing to compare
            // against a whole-day average — at breakfast every member alive would read as a
            // catastrophic drop. The goal bar carries the partial day instead, which is honest
            // about being partway through by construction.
            comparable: latestSteps?.Date != today);
        steps.Goal = baseline?.AvgSteps ?? DefaultStepsGoal;
        // Direction counts here: a member who walked half again as far as usual has not earned a
        // worse rating for it, so only a shortfall costs stars. Null for a day still in progress
        // for the same reason ChangePercent is — the goal bar carries the partial day instead.
        steps.QualityScore = RateAgainstNormal(steps.ChangePercent, shortfallOnly: true);

        // Resting heart rate and sleep are daily summary values, not running totals: the provider
        // either has one for the day or does not. A today reading is a whole reading, so unlike
        // steps it stays comparable against the baseline.
        var latestHeartRate = LatestWith(newestFirst, l => l.RestingHeartRate);
        var heartRate = BuildMetric(
            value: latestHeartRate?.RestingHeartRate,
            baselineValue: baseline?.AvgRestingHeartRate,
            unit: "bpm",
            series: BuildSeries(byDate, today, l => l.RestingHeartRate));
        if (baseline?.AvgRestingHeartRate is int avgHr && baseline.StdDevHeartRate is decimal stdHr)
        {
            heartRate.RangeLow = (int)Math.Round(avgHr - stdHr, MidpointRounding.AwayFromZero);
            heartRate.RangeHigh = (int)Math.Round(avgHr + stdHr, MidpointRounding.AwayFromZero);
        }
        // Both directions count for a resting heart rate — unusually low is as much a departure
        // from this member's own normal as unusually high, so neither is spared the rating.
        heartRate.QualityScore = RateAgainstNormal(heartRate.ChangePercent);

        var latestSleep = LatestWith(newestFirst, l => l.SleepMinutes);
        var sleep = BuildMetric(
            value: latestSleep?.SleepMinutes is int sm ? Math.Round(sm / 60m, 1) : null,
            baselineValue: baseline?.AvgSleepMinutes is int abm ? Math.Round(abm / 60m, 1) : null,
            unit: "hours",
            series: BuildSeries(byDate, today, l => l.SleepMinutes is int m ? Math.Round(m / 60m, 1) : (decimal?)null));
        // Read off the same night as the duration above, so the stars can never describe the
        // quality of one night next to the length of another.
        sleep.QualityScore = latestSleep?.SleepEfficiency switch
        {
            >= 90 => 5,
            >= 80 => 4,
            >= 70 => 3,
            >= 60 => 2,
            not null => 1,
            // Plenty of wearables report a duration but no efficiency at all. Rather than drop the
            // rating for those members entirely, fall back to the length of the night against
            // their own normal — a shorter night than usual is the reading being looked for either
            // way, and like steps a longer one is not marked down for it.
            null => RateAgainstNormal(sleep.ChangePercent, shortfallOnly: true),
        };

        // Temperature carries its own per-day, device-derived baseline (Google Health computes
        // it, not our BaselineCalculationWorker), so it compares against that rather than
        // PatternBaseline — meaningful even during the 30-day learning window.
        var latestTemp = LatestWith(newestFirst, l => l.Temperature);
        var temperature = BuildMetric(
            value: latestTemp?.Temperature,
            baselineValue: latestTemp?.TemperatureBaseline,
            unit: "°C",
            series: BuildSeries(byDate, today, l => l.Temperature));
        // Percent deviation is the wrong comparison unit here — a clinically meaningful ~1°C
        // shift on a ~33-37°C baseline is only 2-3%, which never crosses the shared 30%/50%
        // thresholds BuildMetric just applied. Compare against the device's own per-day stddev
        // (TemperatureVariation) instead, same shape as resting heart rate's RangeLow/RangeHigh.
        if (latestTemp?.Temperature is { } tempValue
            && latestTemp.TemperatureBaseline is { } tempBaseline
            && latestTemp.TemperatureVariation is > 0m and { } tempVariation)
        {
            var deviation = Math.Abs(tempValue - tempBaseline) / tempVariation;
            temperature.Status = deviation switch
            {
                <= 1m => "green",
                <= 2m => "yellow",
                _ => "orange",
            };
            // The same evidence as the status just above, one band finer, so the stars and the
            // pill on this card can never disagree: 4-5 stars is green, 3-2 yellow, 1 orange.
            temperature.QualityScore = deviation switch
            {
                <= 0.5m => 5,
                <= 1m => 4,
                <= 1.5m => 3,
                <= 2m => 2,
                _ => 1,
            };
        }

        // No baseline concept exists for SpO2 yet — shown as a plain reading, not a trend, and
        // with no star rating either: there is nothing to rate it against but an invented normal.
        var latestSpO2 = LatestWith(newestFirst, l => l.SpO2Average);
        var spO2 = BuildMetric(
            value: latestSpO2?.SpO2Average,
            baselineValue: null,
            unit: "%",
            series: BuildSeries(byDate, today, l => l.SpO2Average));

        // Breathing rate has no established-baseline concept yet either, same as SpO2 above.
        var latestBreathing = LatestWith(newestFirst, l => l.BreathingRate);
        var breathingRate = BuildMetric(
            value: latestBreathing?.BreathingRate,
            baselineValue: null,
            unit: "brpm",
            series: BuildSeries(byDate, today, l => l.BreathingRate));

        return new DashboardMetrics
        {
            Steps = steps,
            RestingHeartRate = heartRate,
            Sleep = sleep,
            Temperature = temperature,
            SpO2 = spO2,
            BreathingRate = breathingRate,
        };
    }

    /// <summary>
    /// Rates a reading against this member's own normal out of <see cref="QualityScoreMax"/>, from
    /// exactly the evidence <see cref="BuildMetric"/> already colours the status with — five stars
    /// is "on their normal", one is "a long way off it". The bands nest inside the status
    /// thresholds, so the stars and the status on a card can never contradict each other: 3-5
    /// stars is green, 2 is yellow, 1 is orange. Null whenever there is no comparison to make,
    /// which leaves the card's star row hidden rather than rating a number against nothing.
    /// </summary>
    /// <param name="shortfallOnly">
    /// True for metrics where overshooting the normal is not a departure worth marking down —
    /// steps and sleep duration. Those rate on the shortfall alone, matching the direction the
    /// dashboard's own trend arrow already reads them in.
    /// </param>
    private static int? RateAgainstNormal(decimal? changePercent, bool shortfallOnly = false)
    {
        if (changePercent is not { } change)
            return null;
        if (shortfallOnly && change >= 0)
            return QualityScoreMax;

        return Math.Abs(change) switch
        {
            <= 5m => 5,
            <= 15m => 4,
            <= YellowDeviationPercent => 3,
            <= OrangeDeviationPercent => 2,
            _ => 1,
        };
    }

    /// <summary>The most recent day that actually reported this metric, or null when none did.</summary>
    private static ActivityLog? LatestWith<T>(IReadOnlyList<ActivityLog> newestFirst, Func<ActivityLog, T?> select)
        where T : struct =>
        newestFirst.FirstOrDefault(log => select(log).HasValue);

    /// <param name="comparable">
    /// False when the reading covers a period that is not over yet, which makes a
    /// baseline comparison meaningless rather than merely uncertain. Leaves both
    /// <see cref="DashboardMetric.ChangePercent"/> and the derived status unset, so the client
    /// falls back to the card's plain presentation instead of colouring a number it cannot judge.
    /// </param>
    private static DashboardMetric BuildMetric(
        decimal? value, decimal? baselineValue, string unit, List<MetricPoint> series, bool comparable = true)
    {
        decimal? changePercent = null;
        if (comparable && value is not null && baselineValue is > 0)
            changePercent = Math.Round((value.Value - baselineValue.Value) / baselineValue.Value * 100m, 1);

        return new DashboardMetric
        {
            Value = value,
            Baseline = baselineValue,
            ChangePercent = changePercent,
            Unit = unit,
            Status = changePercent is null
                ? "unknown"
                : Math.Abs(changePercent.Value) switch
                {
                    <= YellowDeviationPercent => "green",
                    <= OrangeDeviationPercent => "yellow",
                    _ => "orange",
                },
            Series = series,
        };
    }

    private static List<MetricPoint> BuildSeries(
        Dictionary<DateOnly, ActivityLog> byDate, DateOnly today, Func<ActivityLog, decimal?> selector)
    {
        var series = new List<MetricPoint>(SeriesDays);
        for (var offset = SeriesDays - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            series.Add(new MetricPoint
            {
                Date = date,
                Value = byDate.TryGetValue(date, out var log) ? selector(log) : null,
            });
        }
        return series;
    }

    public static string ComputeHealthStatus(List<Alert> unresolvedAlerts, bool isLearning, DashboardMetrics? metrics)
    {
        if (unresolvedAlerts.Count > 0)
        {
            var worst = unresolvedAlerts.Max(a => a.Severity);
            if (worst >= AlertSeverity.Yellow)
                return SeverityLabel(worst);
        }
        return isLearning || metrics is null ? "unknown" : "green";
    }

    public static string SeverityLabel(AlertSeverity severity) =>
        severity.ToString().ToLowerInvariant();

    /// <summary>
    /// Deterministic data-pipeline freshness, independent of <see cref="ComputeHealthStatus"/>'s
    /// clinical severity: red/amber describe a data gap (no sync recently), blue/green describe
    /// whether the most recent sync has actually been assessed yet. Never non-deterministic —
    /// unlike the rotating flavour copy this replaced, the same inputs always produce the same
    /// tier and message.
    /// </summary>
    public static (string Tier, string Message) ComputeDataFreshness(
        DateTime? lastSyncedAt, DateTime? lastAssessedAt, DateTime now, string firstName)
    {
        if (lastSyncedAt is not { } synced)
            return ("red", $"No data from {firstName} yet");

        var staleFor = now - synced;
        if (staleFor >= TimeSpan.FromHours(RedStaleHours))
            return ("red", $"No data from {firstName} in over {RedStaleHours} hours");
        if (staleFor >= TimeSpan.FromHours(AmberStaleHours))
            return ("amber", $"No data from {firstName} in over {AmberStaleHours} hours");

        // "Processed" means an assessment covers data at least as recent as the last sync — not
        // just that *an* assessment exists.
        var isProcessed = lastAssessedAt is { } assessed && assessed >= synced;
        return isProcessed ? ("green", "Data processed") : ("blue", "Data updated");
    }
}
