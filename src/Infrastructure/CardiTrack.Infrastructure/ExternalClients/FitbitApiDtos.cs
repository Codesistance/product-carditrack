namespace CardiTrack.Infrastructure.ExternalClients;

// Activities endpoint: GET /1/user/-/activities/date/{date}.json
public record FitbitActivitiesResult(
    int Steps,
    decimal DistanceKm,
    int ActiveMinutes,
    // Null when the provider has no such data type — never 0, which the merge would treat
    // as a real reading and prefer over another device's genuine value.
    int? SedentaryMinutes,
    int Floors,
    int CaloriesBurned);

// Heart rate endpoint: GET /1/user/-/heart/date/{date}/1d.json
public record FitbitHeartRateResult(
    int? RestingHeartRate,
    int? AvgHeartRate,
    int? MaxHeartRate,
    int? MinHeartRate);

// Sleep endpoint: GET /1/user/-/sleep/date/{date}.json
public record FitbitSleepResult(
    int TotalSleepMinutes,
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
public record FitbitAdditionalMetricsResult(
    decimal? SpO2Average,
    decimal? SpO2Min,
    decimal? SpO2Max,
    decimal? VO2Max,
    decimal? BreathingRate,
    decimal? Temperature);
