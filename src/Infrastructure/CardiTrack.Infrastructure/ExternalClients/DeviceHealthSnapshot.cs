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
    // this API at all (see FitbitAdditionalMetricsResult) and is always null.
    decimal? SpO2Average = null,
    decimal? SpO2Min = null,
    decimal? SpO2Max = null,
    decimal? VO2Max = null,
    int? StressScore = null,
    decimal? BreathingRate = null,
    decimal? Temperature = null
);
