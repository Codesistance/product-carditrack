using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// What a user-defined <see cref="Entities.MetricAlarm"/> watches. Deliberately its own enum
/// rather than a reuse of <see cref="GranularMetric"/>: an alarm names a metric <em>at a grain</em>,
/// and the same reading exists at two grains with different meanings. Minute-grain
/// <see cref="Steps"/> summed over ten minutes answers "are they moving right now"; the daily
/// <see cref="DailySteps"/> column answers "how was yesterday", and an alarm that confused the two
/// would compare a ten-minute figure against a day's baseline.
/// <para>
/// Which store serves each value, which statistics are legal on it, and what a datapoint means
/// there are all declared in <c>AlarmMetricCatalogue</c> — that catalogue is the authority, and
/// validation rejects against it rather than against this list.
/// </para>
/// </summary>
public enum AlarmMetric
{
    // Sub-daily — served from the merged minute series (GranularMetricHour / MetricRollupHourly).

    /// <summary>Heart rate, bpm. Minute samples.</summary>
    [Display(Name = "Heart rate")]
    HeartRate = 1,

    /// <summary>Blood oxygen saturation, %. Sampled roughly every five minutes, so most minutes
    /// of the grid are empty by nature rather than by omission.</summary>
    [Display(Name = "Blood oxygen")]
    SpO2 = 2,

    /// <summary>Steps per minute, summed over the alarm's period.</summary>
    [Display(Name = "Steps (short window)")]
    Steps = 3,

    /// <summary>Active zone minutes, summed over the alarm's period.</summary>
    [Display(Name = "Active zone minutes (short window)")]
    ActiveZoneMinutes = 4,

    /// <summary>Heart rate variability (RMSSD), ms, at sub-daily grain. Sampled only while the
    /// wearer is still enough to measure.</summary>
    [Display(Name = "Heart rate variability (short window)")]
    HeartRateVariability = 5,

    // Daily — served from the merged day row (ActivityLog). One datapoint is one civil day, or
    // one night for the readings derived from sleep.

    /// <summary>Resting heart rate, bpm. A settled daily figure with no sub-daily form.</summary>
    [Display(Name = "Resting heart rate")]
    RestingHeartRate = 20,

    /// <summary>Steps for a whole civil day.</summary>
    [Display(Name = "Daily steps")]
    DailySteps = 21,

    /// <summary>A night's sleep, minutes. Attributed to the civil day the night <b>ended</b> on.</summary>
    [Display(Name = "Sleep duration")]
    SleepMinutes = 22,

    /// <summary>The day's average blood oxygen saturation, %.</summary>
    [Display(Name = "Daily average blood oxygen")]
    DailySpO2Average = 23,

    /// <summary>Overnight heart rate variability (RMSSD), ms — the nightly figure.</summary>
    [Display(Name = "Overnight heart rate variability")]
    OvernightHeartRateVariability = 24,

    /// <summary>Breathing rate while asleep, breaths per minute.</summary>
    [Display(Name = "Overnight breathing rate")]
    OvernightBreathingRate = 25,

    /// <summary>The day's longest unbroken sedentary stretch, minutes, with the night clipped out.</summary>
    [Display(Name = "Longest still stretch")]
    LongestSedentaryStretchMinutes = 26,

    /// <summary>Minutes the day spent above the light heart-rate zone.</summary>
    [Display(Name = "Raised heart-rate minutes")]
    ElevatedZoneMinutes = 27,
}
