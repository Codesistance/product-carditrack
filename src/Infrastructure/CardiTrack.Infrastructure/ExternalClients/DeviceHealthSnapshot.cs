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
    // Additional health metrics. No provider populates these yet — the Google Health API
    // data-type names for them are unverified pending sandbox access (see FitbitApiClient),
    // so clients leave them null rather than guess. They are declared here so the contract
    // really is 1:1 with ActivityLog and a provider can fill them without a signature change.
    decimal? SpO2Average = null,
    decimal? SpO2Min = null,
    decimal? SpO2Max = null,
    decimal? VO2Max = null,
    int? StressScore = null,
    decimal? BreathingRate = null,
    decimal? Temperature = null
);
