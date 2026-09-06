using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>Which store answers for a metric, and therefore what one datapoint is.</summary>
public enum AlarmMetricSource
{
    /// <summary>The merged minute series. A datapoint is the alarm's period in minutes.</summary>
    Granular = 1,

    /// <summary>The merged day row. A datapoint is one civil day — or one night, for the readings
    /// derived from sleep, which are filed under the day the night ended on.</summary>
    Daily = 2,
}

/// <summary>
/// Everything about one metric an alarm can be built on: where its readings come from, which
/// statistics mean anything on it, how long a datapoint may cover, what an absolute threshold may
/// be set to, and whether it can be expressed against the member's own baseline.
/// </summary>
/// <param name="Unit">The unit shown beside the threshold. Also what the alert copy quotes.</param>
/// <param name="MinThreshold">
/// The lower bound an absolute threshold may take. Bounds exist because an unclamped field is an
/// alarm-fatigue generator: a caregiver who types 60 into a low-heart-rate alarm — the textbook
/// bradycardia line — will be paged every night by an ordinary sleeping heart. Apple and Fitbit
/// both constrain their equivalents (Apple: high 100-150, low 40-50) for the same reason.
/// </param>
/// <param name="Backing">
/// The granular series that serves this metric, for <see cref="AlarmMetricSource.Granular"/> rows;
/// null for daily ones.
/// </param>
/// <param name="AccumulatesThroughDay">
/// For daily rows: whether the day's value is only a day's worth once the day is over. Steps,
/// raised-heart-rate minutes and the longest still stretch all climb from zero as the day goes on,
/// so today's row is a partial figure until midnight and an alarm must judge the last completed
/// day instead. A sleep-derived reading or a resting heart rate is whole as soon as it is filed.
/// </param>
public sealed record AlarmMetricDefinition(
    AlarmMetric Metric,
    string Title,
    string Unit,
    AlarmMetricSource Source,
    IReadOnlyList<AlarmStatistic> Statistics,
    IReadOnlyList<int> PeriodMinutes,
    decimal MinThreshold,
    decimal MaxThreshold,
    bool SupportsBaselinePercent,
    bool SupportsBaselineSigma,
    GranularMetric? Backing = null,
    bool AccumulatesThroughDay = false);

/// <summary>
/// The authority on what an alarm may be built from. Validation rejects against this rather than
/// against the raw enums, because most metric x statistic x period combinations are meaningless:
/// a five-minute window over <see cref="AlarmMetric.RestingHeartRate"/> asks for a sub-daily form
/// of a figure that only exists once a day, and a <see cref="AlarmStatistic.Sum"/> of heart rates
/// is a number with no physical meaning. The mobile builder reads the same catalogue over the API,
/// so an illegal combination is unreachable in the UI rather than merely refused by the server.
/// <para>
/// Sibling to <see cref="AlertRuleCatalogue"/>, which does the same job for the nine built-in rules.
/// </para>
/// </summary>
public static class AlarmMetricCatalogue
{
    /// <summary>Periods a sub-daily alarm may use. Floored at five minutes because ingestion polls
    /// every ten (<c>WearableSyncWorker</c>) — an alarm cannot outrun the data reaching us, and a
    /// one-minute option would promise a latency this product does not have.</summary>
    public static readonly IReadOnlyList<int> SubDailyPeriods = Array.AsReadOnly(new[] { 5, 10, 15, 30, 60 });

    /// <summary>One civil day, the only period a daily metric offers.</summary>
    public const int DailyPeriodMinutes = 1440;

    private static readonly IReadOnlyList<int> DailyPeriods = Array.AsReadOnly(new[] { DailyPeriodMinutes });

    /// <summary>The most datapoints an evaluation range may span, for either source.</summary>
    public const int MaxEvaluationPeriods = 12;

    /// <summary>The longest stretch a sub-daily evaluation range may cover. A bound rather than a
    /// preference: the range decides how much minute data one tick reads per member.</summary>
    public const int MaxSubDailyRangeMinutes = 1440;

    private static readonly IReadOnlyList<AlarmStatistic> LevelStatistics =
        Array.AsReadOnly(new[] { AlarmStatistic.Average, AlarmStatistic.Minimum, AlarmStatistic.Maximum, AlarmStatistic.Latest });

    private static readonly IReadOnlyList<AlarmStatistic> CountStatistics =
        Array.AsReadOnly(new[] { AlarmStatistic.Sum, AlarmStatistic.Maximum });

    private static readonly IReadOnlyList<AlarmStatistic> DailyStatistics =
        Array.AsReadOnly(new[] { AlarmStatistic.Latest });

    private static readonly IReadOnlyList<AlarmMetricDefinition> DefinitionsInternal =
        Array.AsReadOnly(new[]
        {
            // Sub-daily.
            new AlarmMetricDefinition(AlarmMetric.HeartRate, "Heart rate", "bpm",
                AlarmMetricSource.Granular, LevelStatistics, SubDailyPeriods,
                30m, 220m, SupportsBaselinePercent: false, SupportsBaselineSigma: false, GranularMetric.HeartRate),

            new AlarmMetricDefinition(AlarmMetric.SpO2, "Blood oxygen", "%",
                AlarmMetricSource.Granular, LevelStatistics, SubDailyPeriods,
                70m, 100m, SupportsBaselinePercent: false, SupportsBaselineSigma: false, GranularMetric.SpO2),

            new AlarmMetricDefinition(AlarmMetric.Steps, "Steps (short window)", "steps",
                AlarmMetricSource.Granular, CountStatistics, SubDailyPeriods,
                0m, 5_000m, SupportsBaselinePercent: false, SupportsBaselineSigma: false, GranularMetric.Steps),

            new AlarmMetricDefinition(AlarmMetric.ActiveZoneMinutes, "Active zone minutes (short window)", "minutes",
                AlarmMetricSource.Granular, CountStatistics, SubDailyPeriods,
                0m, 60m, SupportsBaselinePercent: false, SupportsBaselineSigma: false, GranularMetric.ActiveZoneMinutes),

            new AlarmMetricDefinition(AlarmMetric.HeartRateVariability, "Heart rate variability (short window)", "ms",
                AlarmMetricSource.Granular, LevelStatistics, SubDailyPeriods,
                1m, 300m, SupportsBaselinePercent: false, SupportsBaselineSigma: false, GranularMetric.HeartRateVariability),

            // Daily. One value per period, so Latest is the only statistic that means anything.
            new AlarmMetricDefinition(AlarmMetric.RestingHeartRate, "Resting heart rate", "bpm",
                AlarmMetricSource.Daily, DailyStatistics, DailyPeriods,
                30m, 150m, SupportsBaselinePercent: true, SupportsBaselineSigma: true),

            new AlarmMetricDefinition(AlarmMetric.DailySteps, "Daily steps", "steps",
                AlarmMetricSource.Daily, DailyStatistics, DailyPeriods,
                0m, 100_000m, SupportsBaselinePercent: true, SupportsBaselineSigma: true,
                AccumulatesThroughDay: true),

            new AlarmMetricDefinition(AlarmMetric.SleepMinutes, "Sleep duration", "minutes",
                AlarmMetricSource.Daily, DailyStatistics, DailyPeriods,
                0m, 1_440m, SupportsBaselinePercent: true, SupportsBaselineSigma: false),

            new AlarmMetricDefinition(AlarmMetric.DailySpO2Average, "Daily average blood oxygen", "%",
                AlarmMetricSource.Daily, DailyStatistics, DailyPeriods,
                70m, 100m, SupportsBaselinePercent: false, SupportsBaselineSigma: false),

            new AlarmMetricDefinition(AlarmMetric.OvernightHeartRateVariability, "Overnight heart rate variability", "ms",
                AlarmMetricSource.Daily, DailyStatistics, DailyPeriods,
                1m, 300m, SupportsBaselinePercent: true, SupportsBaselineSigma: true),

            new AlarmMetricDefinition(AlarmMetric.OvernightBreathingRate, "Overnight breathing rate", "breaths/min",
                AlarmMetricSource.Daily, DailyStatistics, DailyPeriods,
                4m, 40m, SupportsBaselinePercent: true, SupportsBaselineSigma: true),

            new AlarmMetricDefinition(AlarmMetric.LongestSedentaryStretchMinutes, "Longest still stretch", "minutes",
                AlarmMetricSource.Daily, DailyStatistics, DailyPeriods,
                0m, 1_440m, SupportsBaselinePercent: true, SupportsBaselineSigma: false,
                AccumulatesThroughDay: true),

            new AlarmMetricDefinition(AlarmMetric.ElevatedZoneMinutes, "Raised heart-rate minutes", "minutes",
                AlarmMetricSource.Daily, DailyStatistics, DailyPeriods,
                0m, 1_440m, SupportsBaselinePercent: true, SupportsBaselineSigma: false,
                AccumulatesThroughDay: true),
        });

    public static IReadOnlyList<AlarmMetricDefinition> Definitions => DefinitionsInternal;

    private static readonly Dictionary<AlarmMetric, AlarmMetricDefinition> ByMetric =
        DefinitionsInternal.ToDictionary(d => d.Metric);

    public static AlarmMetricDefinition? Find(AlarmMetric metric) =>
        ByMetric.GetValueOrDefault(metric);

    /// <summary>
    /// The member's own average for this metric, from the established baseline — the anchor both
    /// baseline-relative threshold kinds are resolved against. Null where the baseline carries no
    /// figure for the metric, which is what makes such an alarm report insufficient data rather
    /// than firing against a number it does not have.
    /// </summary>
    public static decimal? BaselineAverage(AlarmMetric metric, PatternBaseline baseline) => metric switch
    {
        AlarmMetric.RestingHeartRate => baseline.AvgRestingHeartRate,
        AlarmMetric.DailySteps => baseline.AvgSteps,
        AlarmMetric.SleepMinutes => baseline.AvgSleepMinutes,
        AlarmMetric.OvernightHeartRateVariability => baseline.AvgHeartRateVariabilityMs,
        AlarmMetric.OvernightBreathingRate => baseline.AvgOvernightBreathingRate,
        AlarmMetric.LongestSedentaryStretchMinutes => baseline.AvgLongestSedentaryStretchMinutes,
        AlarmMetric.ElevatedZoneMinutes => baseline.AvgElevatedZoneMinutes,
        _ => null,
    };

    /// <summary>
    /// The member's own sample standard deviation for this metric. Only four metrics carry one —
    /// the rest offer percent-of-baseline thresholds but not sigma ones, and the catalogue says so
    /// per row rather than leaving a caller to discover it as a null at evaluation time.
    /// </summary>
    public static decimal? BaselineStdDev(AlarmMetric metric, PatternBaseline baseline) => metric switch
    {
        AlarmMetric.RestingHeartRate => baseline.StdDevHeartRate,
        AlarmMetric.DailySteps => baseline.StdDevSteps,
        AlarmMetric.OvernightHeartRateVariability => baseline.StdDevHeartRateVariability,
        AlarmMetric.OvernightBreathingRate => baseline.StdDevOvernightBreathingRate,
        _ => null,
    };

    /// <summary>
    /// One day row's value for a daily metric, or null where the day did not measure it. Null is
    /// "not measured" and never zero: a day the watch spent in a drawer is not a day of no steps,
    /// and the missing-data treatment — not the arithmetic — decides what absence counts as.
    /// </summary>
    public static double? DailyValue(AlarmMetric metric, ActivityLog log) => metric switch
    {
        AlarmMetric.RestingHeartRate => log.RestingHeartRate,
        AlarmMetric.DailySteps => log.Steps,
        AlarmMetric.SleepMinutes => log.SleepMinutes,
        AlarmMetric.DailySpO2Average => (double?)log.SpO2Average,
        AlarmMetric.OvernightHeartRateVariability => (double?)log.HeartRateVariabilityMs,
        AlarmMetric.OvernightBreathingRate => (double?)log.OvernightBreathingRate,
        AlarmMetric.LongestSedentaryStretchMinutes => log.LongestSedentaryStretchMinutes,
        AlarmMetric.ElevatedZoneMinutes => BaselineCalculator.ElevatedZoneMinutes(log),
        _ => null,
    };
}
