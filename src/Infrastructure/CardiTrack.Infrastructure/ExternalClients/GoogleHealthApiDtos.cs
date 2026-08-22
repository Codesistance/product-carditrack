namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// One day of activity totals. Every field is null when the provider reported nothing for that
/// day — never 0, which the multi-device merge would treat as a real reading and prefer over
/// another device's genuine value, and which the baseline would average in as a day of stillness.
/// An explicit zero from the provider (a worn device, no activity) is kept as 0 and is a different
/// fact entirely.
/// </summary>
public record GoogleHealthActivitiesResult(
    int? Steps,
    decimal? DistanceKm,
    int? ActiveMinutes,
    int? SedentaryMinutes,
    int? Floors,
    int? CaloriesBurned);

// Heart rate: Google Health API v4 `heart-rate` Sample type via dataPoints:dailyRollUp, plus the
// `daily-resting-heart-rate` Daily type via list (see GoogleHealthApiClient for the method table).
public record GoogleHealthHeartRateResult(
    int? RestingHeartRate,
    int? AvgHeartRate,
    int? MaxHeartRate,
    int? MinHeartRate);

// Sleep: Google Health API v4 `sleep` Session type via list with a civil-time filter.
// TotalSleepMinutes is null when no session was recorded for the day — an unworn or unsynced
// device, not a sleepless night. See GoogleHealthActivitiesResult for why that distinction is kept.
public record GoogleHealthSleepResult(
    int? TotalSleepMinutes,
    int? SleepEfficiency,
    DateTime? SleepStartTime,
    DateTime? SleepEndTime,
    int? DeepSleepMinutes,
    int? LightSleepMinutes,
    int? RemSleepMinutes,
    int? AwakeMinutes);

/// <summary>
/// The metrics beyond activity, heart rate and sleep. Every one is null on a device that does not
/// record it — a great many Fitbits derive none of these — so null here means "not measured", never
/// zero, which the multi-device merge would treat as a real reading.
/// </summary>
/// <remarks>
/// There is deliberately no StressScore. Google Health API v4 exposes no stress or readiness data
/// type at all, and its `mindfulness` and `logged_symptoms` scopes are write-only, so nothing on
/// this API can populate <c>ActivityLog.StressScore</c>. The column is left null rather than filled
/// with a number CardiTrack invented and then presented beside measured values.
/// </remarks>
/// <param name="HeartRateVariabilityMs">
/// Overnight RMSSD in milliseconds, from the <c>daily-heart-rate-variability</c> Daily record —
/// the one metric here read under a bundle every connection already holds that CardiTrack had
/// never asked for. Null on a device that derives none, like every other field on this record.
/// </param>
public record GoogleHealthAdditionalMetricsResult(
    decimal? SpO2Average,
    decimal? SpO2Min,
    decimal? SpO2Max,
    decimal? VO2Max,
    decimal? BreathingRate,
    decimal? Temperature,
    decimal? TemperatureBaseline,
    decimal? TemperatureVariation,
    decimal? HeartRateVariabilityMs);
