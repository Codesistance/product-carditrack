namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Provider-neutral health data snapshot for one member for one day.
/// Fields map 1:1 to ActivityLog nullable columns — providers return null for metrics they don't support.
/// </summary>
public record DeviceHealthSnapshot(
    // Activity
    int? Steps,
    decimal? DistanceKm,
    int? ActiveMinutes,
    int? SedentaryMinutes,
    int? Floors,
    int? CaloriesBurned,
    // Heart rate
    int? RestingHeartRate,
    int? AvgHeartRate,
    int? MaxHeartRate,
    int? MinHeartRate,
    // Sleep
    int? TotalSleepMinutes,
    int? SleepEfficiency,
    DateTime? SleepStartTime,
    DateTime? SleepEndTime,
    int? DeepSleepMinutes,
    int? LightSleepMinutes,
    int? RemSleepMinutes,
    int? AwakeMinutes,
    // Additional health metrics. Field names are confirmed against the v4 discovery document;
    // whether a given wearer's device populates them is a per-device fact, so a client that finds
    // nothing leaves them null rather than substituting a figure. StressScore has no source on
    // this API at all (see GoogleHealthAdditionalMetricsResult) and is always null.
    decimal? SpO2Average = null,
    decimal? SpO2Min = null,
    decimal? SpO2Max = null,
    decimal? VO2Max = null,
    int? StressScore = null,
    decimal? BreathingRate = null,
    decimal? Temperature = null,
    decimal? TemperatureBaseline = null,
    decimal? TemperatureVariation = null
)
{
    /// <summary>
    /// True when the provider reported anything at all for the day. The history backfill checks
    /// this before storing: an all-null row would still count as a "data day" to the baseline
    /// coverage gate and the dashboard's days-captured figure, letting an empty history fake its
    /// way past both.
    /// </summary>
    public bool HasAnyData =>
        Steps is not null || DistanceKm is not null || ActiveMinutes is not null ||
        SedentaryMinutes is not null || Floors is not null || CaloriesBurned is not null ||
        RestingHeartRate is not null || AvgHeartRate is not null || MaxHeartRate is not null ||
        MinHeartRate is not null || TotalSleepMinutes is not null || SleepEfficiency is not null ||
        SleepStartTime is not null || SleepEndTime is not null || DeepSleepMinutes is not null ||
        LightSleepMinutes is not null || RemSleepMinutes is not null || AwakeMinutes is not null ||
        SpO2Average is not null || SpO2Min is not null || SpO2Max is not null ||
        VO2Max is not null || StressScore is not null || BreathingRate is not null ||
        Temperature is not null || TemperatureBaseline is not null || TemperatureVariation is not null;
}
