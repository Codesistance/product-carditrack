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
public record GoogleHealthAdditionalMetricsResult(
    decimal? SpO2Average,
    decimal? SpO2Min,
    decimal? SpO2Max,
    decimal? VO2Max,
    decimal? BreathingRate,
    decimal? Temperature,
    decimal? TemperatureBaseline,
    decimal? TemperatureVariation);

/// <summary>
/// The body-and-autonomic metrics added in the 2026-08 device-data sweep: heart rate variability,
/// weight, and blood glucose. All three read under
/// <c>googlehealth.health_metrics_and_measurements.readonly</c>, which every connection already
/// holds, so they need no re-consent — unlike the rhythm pair in <see cref="GoogleHealthRhythmResult"/>.
/// </summary>
/// <remarks>
/// Null is "not measured" here exactly as it is everywhere else in this client. It carries more
/// weight for these three than for the metrics above, because two of them depend on hardware the
/// wearer may simply not own: weight needs a connected scale and glucose a meter or CGM feeding
/// the same account. A wearer with neither leaves those columns null forever, which is a fact
/// about their kit and never a sync fault.
/// </remarks>
/// <param name="HeartRateVariabilityMs">
/// Overnight RMSSD in milliseconds, from the <c>daily-heart-rate-variability</c> Daily record.
/// RMSSD rather than the record's deep-sleep or entropy siblings: it is the figure the design's
/// SSA mapping names (docs/llm_design.md), and the one every published HRV yardstick is written in.
/// </param>
/// <param name="WeightKg">
/// The day's average weight in kilograms, converted from the rollup's grams. Averaged rather than
/// last-of-day because the rollup exposes only <c>weightGramsAvg</c>; on a day with a single
/// weighing — the normal case — the two are the same number.
/// </param>
public record GoogleHealthBodyMetricsResult(
    decimal? HeartRateVariabilityMs,
    decimal? WeightKg,
    decimal? BloodGlucoseAverage,
    decimal? BloodGlucoseMin,
    decimal? BloodGlucoseMax);

/// <summary>
/// One civil day's rhythm events: ECG readings the wearer took, and irregular-rhythm notifications
/// the watch raised on its own.
/// </summary>
/// <remarks>
/// Counts, never waveforms. The <c>electrocardiogram</c> data type carries a
/// <c>waveformSamples</c> array — 30 seconds of lead-I voltages at the device's sampling frequency
/// — and this client asks the API not to send it (a <c>fields</c> partial-response selector on the
/// request). Storing a diagnostic-grade trace would multiply what a breach exposes and buy nothing
/// the classification does not already say; CardiTrack reports that the wearer took a reading and
/// what the watch made of it, which is what a family caregiver can act on.
/// <para>
/// Both types are Google-flagged SaMD features and both sit behind their own OAuth scopes
/// (<c>googlehealth.ecg.readonly</c>, <c>googlehealth.irn.readonly</c>). Every connection
/// authorised before those scopes were added lacks them until the wearer reconnects, which is why
/// a null count is never treated as a fault — the same discipline
/// <see cref="CardiTrack.Application.Services.Notifications.DeviceScopes.GrantsSettings"/>
/// established for battery.
/// </para>
/// </remarks>
/// <param name="EcgReadings">
/// How many ECG sessions the wearer recorded that day, or null when the type could not be read at
/// all. Zero is a real answer — a day with the scope granted and no reading taken.
/// </param>
/// <param name="EcgAtrialFibrillationReadings">
/// How many of those sessions the device classified <c>ATRIAL_FIBRILLATION</c>. Only that member
/// counts: <c>INCONCLUSIVE*</c>, <c>UNREADABLE</c> and <c>NOT_ANALYZED</c> mean the device declined
/// to judge, and reading a declined judgement as a positive one would page a family over a reading
/// their watch explicitly refused to call.
/// </param>
/// <param name="IrregularRhythmNotifications">
/// How many irregular-rhythm notifications the watch raised that day. The record exists only when
/// the check fired, so any count above zero is the device telling the wearer it saw a rhythm
/// consistent with AFib — not CardiTrack inferring one.
/// </param>
public record GoogleHealthRhythmResult(
    int? EcgReadings,
    int? EcgAtrialFibrillationReadings,
    int? IrregularRhythmNotifications);
