using System.Globalization;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// One metric's trajectory: where it sits now, which way it is going, and how far that is from the
/// member's own learned normal over each window they have one for.
/// </summary>
/// <param name="Metric">The label the narrative uses — plain words, not a column name.</param>
/// <param name="Unit">What the figures are in.</param>
/// <param name="RecentAverage">Mean over the last <see cref="TrendFeatureCalculator.MovingAverageDays"/> measured days.</param>
/// <param name="ChangePerDay">
/// Least-squares slope over the trend window, per day. Positive is rising. Null when too few days
/// carry a reading for a line through them to mean anything.
/// </param>
/// <param name="DeviationPercent">
/// <see cref="RecentAverage"/> against each baseline window the member has, keyed by its length in
/// days. Signed whole percent, negative below the baseline.
/// </param>
/// <param name="MeasuredDays">How many days in the window actually carried a reading.</param>
public sealed record TrendFeature(
    string Metric,
    string Unit,
    decimal? RecentAverage,
    decimal? ChangePerDay,
    IReadOnlyDictionary<int, decimal> DeviationPercent,
    int MeasuredDays);

/// <summary>Average steps on each weekday over the window — the seasonality the design names.</summary>
public sealed record WeekdayShape(DayOfWeek Day, decimal AverageSteps, int MeasuredDays);

/// <summary>Everything the trend narrative is written from. Every number here was computed in .NET.</summary>
public sealed record TrendFeatures(
    DateOnly Through,
    int DaysOfHistory,
    IReadOnlyList<TrendFeature> Features,
    IReadOnlyList<WeekdayShape> WeekdayShape);

/// <summary>
/// The deterministic half of trend interpretation (<c>docs/llm_design.md</c> — "Deterministic trend
/// features"): moving averages, slopes and deviations against the member's own multi-window
/// baselines, computed here so that the model that reads them is interpreting arithmetic rather
/// than doing any.
/// </summary>
/// <remarks>
/// <para>
/// This is what replaced the per-user LSTM and its calibrated risk scores, dropped 2026-08-10.
/// Nothing here estimates, predicts or scores: every figure is a mean, a least-squares slope, or a
/// ratio of one to another, and each is reproducible from the same rows months later. That is the
/// property the risk model could not offer and the reason the narrative built on this can be
/// audited at all.
/// </para>
/// <para>
/// It reads <c>ActivityLog</c> rows and the <c>PatternBaseline</c> windows the Worker already
/// computes — all of <see cref="BaselineCalculator.SupportedPeriods"/>. The design sketch named
/// "multi-horizon rollups", but no daily or weekly rollup table exists; the daily log <em>is</em>
/// the daily horizon, and the longer horizons are the 60- and 90-day baselines. Building rollup
/// tables to say what those already say would be a schema for a sentence.
/// </para>
/// </remarks>
public static class TrendFeatureCalculator
{
    /// <summary>
    /// Days of history a member needs before a trend is narrated at all. The same 30 that decides
    /// whether a baseline is established (<see cref="BaselineProgress.PeriodDays"/>): below it
    /// there is no learned normal to measure a trajectory against, and a slope through three weeks
    /// of a new wearer's data describes them getting used to the watch.
    /// </summary>
    public const int MinimumDaysForTrend = 30;

    /// <summary>The window "recently" means — one week, so a single odd day cannot carry it.</summary>
    public const int MovingAverageDays = 7;

    /// <summary>
    /// How far back the slope is fitted. Four weeks rather than the full baseline window: the
    /// question a trend answers is where someone is heading now, and a quarter of a year of data
    /// flattens exactly the recent movement that is worth saying out loud.
    /// </summary>
    public const int SlopeWindowDays = 28;

    /// <summary>Days inside the slope window that must carry a reading before a line is fitted.</summary>
    public const int MinimumDaysForSlope = 10;

    /// <summary>
    /// The features for one member, or null when they have too little history for any of it to
    /// mean anything — the cold-start case, which is the learning state rather than a trend.
    /// </summary>
    public static TrendFeatures? Compute(
        IReadOnlyList<ActivityLog> logs,
        IReadOnlyList<PatternBaseline> baselines,
        DateOnly through)
    {
        // One row per date before anything is counted or averaged. Ingestion upserts per
        // (DeviceConnection, Date), so a member wearing two devices has two rows for the same
        // day, and every figure below is a per-day one: a moving average over raw rows weights
        // the days they wore both watches double, the slope bends toward them, and the weekday
        // shape counts those days twice over. The most recently written row per date is the rule
        // BaselineCalculator uses, so the deviations here are measured against a usual drawn the
        // same way.
        var ordered = logs
            .GroupBy(l => l.Date)
            .Select(g => g.OrderByDescending(l => l.UpdatedDate ?? l.CreatedDate).First())
            .OrderBy(l => l.Date)
            .ToList();

        // Days carrying a metric this calculator actually reads, not days with a row. A member
        // whose logs hold only distance and SpO2 has thirty rows and nothing to compute from; the
        // count alone would clear the gate, every feature would come back empty, and the pass
        // would spend a model call asking for a narrative of no figures at all.
        var measuredDays = ordered.Count(HasTrendMetric);
        if (measuredDays < MinimumDaysForTrend)
            return null;

        var features = new List<TrendFeature>
        {
            Feature("Resting heart rate", "bpm", ordered, baselines, through,
                l => l.RestingHeartRate, b => b.AvgRestingHeartRate),
            Feature("Steps", "steps a day", ordered, baselines, through,
                l => l.Steps, b => b.AvgSteps),
            Feature("Sleep", "minutes a night", ordered, baselines, through,
                l => l.SleepMinutes, b => b.AvgSleepMinutes),
            Feature("Active minutes", "minutes a day", ordered, baselines, through,
                l => l.ActiveMinutes, b => b.AvgActiveMinutes),
            Feature("Overnight heart rate variability", "ms", ordered, baselines, through,
                l => l.HeartRateVariabilityMs, b => b.AvgHeartRateVariabilityMs),
            Feature("Breathing rate asleep", "breaths a minute", ordered, baselines, through,
                l => l.OvernightBreathingRate, b => b.AvgOvernightBreathingRate),
        };

        var populated = features.Where(f => f.RecentAverage is not null).ToList();

        // And nothing recent to say is the same answer as nothing at all: a member with a long
        // history whose last week is entirely unmeasured would otherwise reach the model with a
        // window header and no rows under it.
        if (populated.Count == 0)
            return null;

        return new TrendFeatures(through, measuredDays, populated, WeekdayShapeOf(ordered));
    }

    /// <summary>Whether a day carries any of the readings the features are computed from.</summary>
    private static bool HasTrendMetric(ActivityLog log) =>
        log.RestingHeartRate is not null
        || log.Steps is not null
        || log.SleepMinutes is not null
        || log.ActiveMinutes is not null
        || log.HeartRateVariabilityMs is not null
        || log.OvernightBreathingRate is not null;

    private static TrendFeature Feature(
        string metric,
        string unit,
        IReadOnlyList<ActivityLog> ordered,
        IReadOnlyList<PatternBaseline> baselines,
        DateOnly through,
        Func<ActivityLog, decimal?> read,
        Func<PatternBaseline, decimal?> readBaseline)
    {
        var recentFrom = through.AddDays(-(MovingAverageDays - 1));
        var recent = ordered
            .Where(l => l.Date >= recentFrom && l.Date <= through)
            .Select(read)
            .OfType<decimal>()
            .ToList();

        var slopeFrom = through.AddDays(-(SlopeWindowDays - 1));
        var slopePoints = ordered
            .Where(l => l.Date >= slopeFrom && l.Date <= through)
            .Select(l => (Day: (decimal)l.Date.DayNumber, Value: read(l)))
            .Where(p => p.Value is not null)
            .Select(p => (p.Day, Value: p.Value!.Value))
            .ToList();

        var recentAverage = recent.Count > 0 ? Math.Round(recent.Average(), 1) : (decimal?)null;

        return new TrendFeature(
            metric,
            unit,
            recentAverage,
            Slope(slopePoints),
            Deviations(recentAverage, baselines, readBaseline),
            recent.Count);
    }

    /// <summary>
    /// The recent average against each baseline window the member actually has, as signed whole
    /// percent. Keyed by window length so the narrative can say which normal it is comparing to —
    /// "3% above their 30-day usual but 9% above their 90-day" is the shape of a slow drift, and
    /// collapsing the windows into one figure would lose exactly that.
    /// </summary>
    /// <remarks>
    /// A window whose baseline holds no figure for this metric is left out rather than reported as
    /// zero: nothing learned is not the same as no change.
    /// </remarks>
    private static IReadOnlyDictionary<int, decimal> Deviations(
        decimal? recentAverage,
        IReadOnlyList<PatternBaseline> baselines,
        Func<PatternBaseline, decimal?> readBaseline)
    {
        var deviations = new Dictionary<int, decimal>();
        if (recentAverage is not { } average)
            return deviations;

        foreach (var baseline in baselines.OrderBy(b => b.PeriodDays))
        {
            if (readBaseline(baseline) is not > 0 || deviations.ContainsKey(baseline.PeriodDays))
                continue;

            var usual = readBaseline(baseline)!.Value;
            deviations[baseline.PeriodDays] = Math.Round((average - usual) / usual * 100m, 0);
        }

        return deviations;
    }

    /// <summary>
    /// The least-squares slope through the points, per day, or null when too few of them carry a
    /// reading. Rounded to two places: this is a change per day in a metric measured to the unit,
    /// and more places would suggest a precision the wearable does not have.
    /// </summary>
    /// <remarks>
    /// Guarded on the denominator rather than only on the count, because a member whose readings
    /// all landed on one day (a backfill) gives every point the same x and no line at all. That
    /// divides by zero rather than failing a count check.
    /// </remarks>
    private static decimal? Slope(IReadOnlyList<(decimal Day, decimal Value)> points)
    {
        if (points.Count < MinimumDaysForSlope)
            return null;

        var meanDay = points.Average(p => p.Day);
        var meanValue = points.Average(p => p.Value);

        decimal covariance = 0;
        decimal variance = 0;
        foreach (var (day, value) in points)
        {
            var offset = day - meanDay;
            covariance += offset * (value - meanValue);
            variance += offset * offset;
        }

        return variance == 0 ? null : Math.Round(covariance / variance, 2);
    }

    private static IReadOnlyList<WeekdayShape> WeekdayShapeOf(IReadOnlyList<ActivityLog> ordered)
    {
        return ordered
            .Where(l => l.Steps is not null)
            .GroupBy(l => l.Date.DayOfWeek)
            .OrderBy(g => g.Key)
            .Select(g => new WeekdayShape(
                g.Key,
                Math.Round(g.Average(l => (decimal)l.Steps!.Value), 0),
                g.Count()))
            .ToList();
    }

    /// <summary>
    /// The features as the lines the prompt carries. Rendered here rather than in the prompt
    /// builder so the arithmetic and its wording stay together: a figure the narrative quotes is
    /// one this file computed and labelled, and there is no second place a percentage could be
    /// worked out differently.
    /// </summary>
    public static string Render(TrendFeatures features)
    {
        var lines = new List<string>
        {
            $"Window ending {features.Through.ToString("O", CultureInfo.InvariantCulture)}, "
            + $"{features.DaysOfHistory} days with readings.",
        };

        foreach (var feature in features.Features)
        {
            var parts = new List<string>
            {
                $"last {MovingAverageDays} days averaged {feature.RecentAverage:0.#} {feature.Unit}",
            };

            if (feature.ChangePerDay is { } slope)
            {
                var direction = slope switch
                {
                    > 0 => "rising",
                    < 0 => "falling",
                    _ => "flat",
                };
                parts.Add($"{direction} about {Math.Abs(slope):0.##} {feature.Unit} a day "
                    + $"over the last {SlopeWindowDays} days");
            }

            foreach (var (window, deviation) in feature.DeviationPercent.OrderBy(d => d.Key))
            {
                parts.Add($"{Math.Abs(deviation):0}% {(deviation < 0 ? "below" : "above")} "
                    + $"their {window}-day usual");
            }

            lines.Add($"- {feature.Metric}: {string.Join("; ", parts)}.");
        }

        if (features.WeekdayShape.Count > 0)
        {
            var busiest = features.WeekdayShape.MaxBy(d => d.AverageSteps)!;
            var quietest = features.WeekdayShape.MinBy(d => d.AverageSteps)!;
            if (busiest.Day != quietest.Day)
            {
                lines.Add($"- Weekday shape: {busiest.Day}s average {busiest.AverageSteps:N0} steps, "
                    + $"{quietest.Day}s {quietest.AverageSteps:N0}.");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
