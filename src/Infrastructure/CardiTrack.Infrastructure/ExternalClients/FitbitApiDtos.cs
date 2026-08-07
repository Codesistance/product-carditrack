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
