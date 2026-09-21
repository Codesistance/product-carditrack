using System.Globalization;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

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

/// <summary>
/// How far back each figure of a trend reads. One preset per <see cref="TrendHorizon"/>, so that a
/// narrative headed "the week just gone" is written from the week just gone rather than from a
/// month-long slope that happens to end in it.
/// </summary>
/// <param name="MinimumDaysOfHistory">
/// Days of readings the member needs before any trend is narrated. The same value at every
/// horizon: it is about whether a learned normal exists to measure against, which does not change
/// because the question got shorter.
/// </param>
/// <param name="AverageDays">How many days back <see cref="TrendFeature.RecentAverage"/> means.</param>
/// <param name="SlopeDays">How far back the least-squares line is fitted.</param>
/// <param name="MinimumDaysForSlope">Days inside <paramref name="SlopeDays"/> that must carry a
/// reading before a line is fitted at all.</param>
public sealed record TrendWindow(
    int MinimumDaysOfHistory,
    int AverageDays,
    int SlopeDays,
    int MinimumDaysForSlope)
{
    /// <summary>
    /// The rolling read the daily pass has always used: a week's average, a four-week slope.
    /// Unchanged from when these were constants, so the daily narrative is the same narrative.
    /// </summary>
    public static readonly TrendWindow Rolling = new(
        TrendFeatureCalculator.MinimumDaysForTrend,
        TrendFeatureCalculator.MovingAverageDays,
        TrendFeatureCalculator.SlopeWindowDays,
        TrendFeatureCalculator.MinimumDaysForSlope);

    /// <summary>
    /// The week just gone, end to end. Both the average and the slope span exactly the seven days
    /// the note is about, and four of them must carry a reading — the Weekbook's four-of-seven,
    /// because a week measured on three days is an unmeasured week whichever surface is
    /// describing it.
    /// </summary>
    /// <remarks>
    /// The <em>threshold</em> is the Weekbook's; what counts toward it is not, and deliberately.
    /// The book describes whatever the week recorded, so a day carrying only distance or SpO2 is
    /// a day that carried readings to it. A trend reads six series and can plot none of those, so
    /// <see cref="TrendFeatureCalculator.CountMeasuredDays"/> asks for a day carrying something it
    /// can actually draw a line through. Each guard is the right bar for the read it gates, and a
    /// member can therefore get a Weekbook and no weekly trend — correctly, because there was
    /// nothing to trend.
    /// </remarks>
    public static readonly TrendWindow Weekly = new(
        TrendFeatureCalculator.MinimumDaysForTrend, 7, 7, 4);

    /// <summary>
    /// A nominal month, for callers with no particular month in hand. Prefer
    /// <see cref="ForMonth"/>, which takes the real length of the month being described.
    /// Fourteen days must carry a reading, matching the Monthbook's own guard.
    /// </summary>
    /// <remarks>
    /// A fixed thirty is what <c>TrendAwareness.MonthWindowDays</c> draws its chart over, so that
    /// every month's chart is the same width and a February does not read as a quieter month than
    /// a March for being shorter. That reasoning is about a picture and does not carry to a
    /// sentence: the narrative <em>says</em> "the month that has just ended", and a thirty-day
    /// window ending on the last of February would be describing two days of January as well.
    /// A chart makes no claim about which month it is; this does.
    /// </remarks>
    public static readonly TrendWindow Monthly = new(
        TrendFeatureCalculator.MinimumDaysForTrend, 30, 30, 14);

    /// <summary>
    /// The month just gone, at its own length — 28, 29, 30 or 31 days, as
    /// <c>JournalPeriod.DayCount</c> reports it. The window ends on the month's last day, so its
    /// length is the only thing that decides whether it starts on the first.
    /// </summary>
    public static TrendWindow ForMonth(int dayCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dayCount, 1);
        return Monthly with { AverageDays = dayCount, SlopeDays = dayCount };
    }

    /// <summary>
    /// The preset for one horizon. <see cref="TrendHorizon.Monthly"/> comes back nominal —
    /// a caller that knows which month it is describing should use <see cref="ForMonth"/>.
    /// </summary>
    public static TrendWindow For(TrendHorizon horizon) => horizon switch
    {
        TrendHorizon.Weekly => Weekly,
        TrendHorizon.Monthly => Monthly,
        _ => Rolling,
    };
}

/// <summary>Everything the trend narrative is written from. Every number here was computed in .NET.</summary>
/// <param name="Window">
/// The spans these figures were drawn over. Carried on the result rather than read back off the
/// calculator's constants, so <see cref="TrendFeatureCalculator.Render"/> cannot label a weekly
/// average as a monthly one — the label and the arithmetic come from the same object.
/// </param>
public sealed record TrendFeatures(
    DateOnly Through,
    int DaysOfHistory,
    IReadOnlyList<TrendFeature> Features,
    IReadOnlyList<WeekdayShape> WeekdayShape,
    TrendWindow Window);

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
    /// The features for one member over the rolling window the daily pass reads.
    /// </summary>
    public static TrendFeatures? Compute(
        IReadOnlyList<ActivityLog> logs,
        IReadOnlyList<PatternBaseline> baselines,
        DateOnly through)
        => Compute(logs, baselines, through, TrendWindow.Rolling);

    /// <summary>
    /// The features for one member over <paramref name="window"/>, or null when they have too
    /// little history for any of it to mean anything — the cold-start case, which is the learning
    /// state rather than a trend.
    /// </summary>
    /// <remarks>
    /// <paramref name="logs"/> is expected to cover well more than the window: the history gate
    /// and the baseline deviations both read the whole span supplied, and only the average and the
    /// slope are cut to the window. A weekly narrative is still refused to a member with three
    /// weeks of readings, which is the point — the horizon changes what is described, never how
    /// much history it takes before anything is.
    /// </remarks>
    public static TrendFeatures? Compute(
        IReadOnlyList<ActivityLog> logs,
        IReadOnlyList<PatternBaseline> baselines,
        DateOnly through,
        TrendWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // One row per date before anything is counted or averaged. Ingestion upserts per
        // (DeviceConnection, Date), so a member wearing two devices has two rows for the same
        // day, and every figure below is a per-day one: a moving average over raw rows weights
        // the days they wore both watches double, the slope bends toward them, and the weekday
        // shape counts those days twice over. The most recently written row per date is the rule
        // BaselineCalculator uses, so the deviations here are measured against a usual drawn the
        // same way.
        var ordered = logs
            .GroupBy(l => l.Date)
            .Select(WinningRow)
            .OrderBy(l => l.Date)
            .ToList();

        // Days carrying a metric this calculator actually reads, not days with a row. A member
        // whose logs hold only distance and SpO2 has thirty rows and nothing to compute from; the
        // count alone would clear the gate, every feature would come back empty, and the pass
        // would spend a model call asking for a narrative of no figures at all.
        var measuredDays = ordered.Count(HasTrendMetric);
        if (measuredDays < window.MinimumDaysOfHistory)
            return null;

        var features = new List<TrendFeature>
        {
            Feature("Resting heart rate", "bpm", ordered, baselines, through, window,
                l => l.RestingHeartRate, b => b.AvgRestingHeartRate),
            Feature("Steps", "steps a day", ordered, baselines, through, window,
                l => l.Steps, b => b.AvgSteps),
            Feature("Sleep", "minutes a night", ordered, baselines, through, window,
                l => l.SleepMinutes, b => b.AvgSleepMinutes),
            // Same name as the card and the books, from ActivityMetricNaming — a test holds this
            // one and the card together, and the shared constant holds the other three.
            Feature(ActivityMetricNaming.Label, ActivityMetricNaming.MinutesPerDayUnit,
                ordered, baselines, through, window,
                l => l.ActiveMinutes, b => b.AvgActiveMinutes),
            Feature("Overnight heart rate variability", "ms", ordered, baselines, through, window,
                l => l.HeartRateVariabilityMs, b => b.AvgHeartRateVariabilityMs),
            Feature("Breathing rate asleep", "breaths a minute", ordered, baselines, through, window,
                l => l.OvernightBreathingRate, b => b.AvgOvernightBreathingRate),
        };

        var populated = features.Where(f => f.RecentAverage is not null).ToList();

        // And nothing recent to say is the same answer as nothing at all: a member with a long
        // history whose last week is entirely unmeasured would otherwise reach the model with a
        // window header and no rows under it.
        if (populated.Count == 0)
            return null;

        return new TrendFeatures(through, measuredDays, populated, WeekdayShapeOf(ordered), window);
    }

    /// <summary>
    /// How many distinct days in <paramref name="logs"/> carry a reading these features are
    /// computed from — the coverage a caller checks a period against before spending a model call
    /// on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct days, not rows, and for the reason <see cref="Compute"/> gives at more length: a
    /// member wearing two watches has two rows for the same day, and counting rows would let four
    /// days of two-device readings clear a bar meant to mean seven.
    /// </para>
    /// <para>
    /// It reduces to one row per date <em>the same way</em> <see cref="Compute"/> does — most
    /// recently written wins — and then asks whether that row carries a reading. Counting a date
    /// because any row for it did would let this disagree with the features: where the winning row
    /// holds no trend metric, <see cref="Compute"/> has nothing for that day, and a coverage check
    /// that said otherwise would pass a period straight into an empty read.
    /// </para>
    /// </remarks>
    public static int CountMeasuredDays(IEnumerable<ActivityLog> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        return logs
            .GroupBy(l => l.Date)
            .Select(WinningRow)
            .Count(HasTrendMetric);
    }

    /// <summary>
    /// The row that speaks for a date when more than one device reported it: the most recently
    /// written, which is the rule <c>BaselineCalculator</c> uses, so the deviations here are
    /// measured against a usual drawn the same way.
    /// </summary>
    private static ActivityLog WinningRow(IGrouping<DateOnly, ActivityLog> day) =>
        day.OrderByDescending(l => l.UpdatedDate ?? l.CreatedDate).First();

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
        TrendWindow window,
        Func<ActivityLog, decimal?> read,
        Func<PatternBaseline, decimal?> readBaseline)
    {
        var recentFrom = through.AddDays(-(window.AverageDays - 1));
        var recent = ordered
            .Where(l => l.Date >= recentFrom && l.Date <= through)
            .Select(read)
            .OfType<decimal>()
            .ToList();

        var slopeFrom = through.AddDays(-(window.SlopeDays - 1));
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
            Slope(slopePoints, window.MinimumDaysForSlope),
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
    private static decimal? Slope(IReadOnlyList<(decimal Day, decimal Value)> points, int minimumDays)
    {
        if (points.Count < minimumDays)
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
                $"last {features.Window.AverageDays} days averaged {feature.RecentAverage:0.#} {feature.Unit}",
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
                    + $"over the last {features.Window.SlopeDays} days");
            }

            foreach (var (window, deviation) in feature.DeviationPercent.OrderBy(d => d.Key))
            {
                // Zero is its own case, not the positive one. A recent average that lands exactly
                // on a baseline used to reach the model as "0% above their 30-day usual", which
                // reads as a direction and is the opposite of what a flat metric means — in a
                // prompt whose whole premise is that the model may not work a comparison out for
                // itself and must state what it is given.
                parts.Add(deviation == 0
                    ? $"level with their {window}-day usual"
                    : $"{Math.Abs(deviation):0}% {(deviation < 0 ? "below" : "above")} "
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
