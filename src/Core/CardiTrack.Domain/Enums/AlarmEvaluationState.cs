using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// The three states a metric alarm can be in, named as CloudWatch names them. An alert is raised
/// on the <b>transition</b> into <see cref="Alarm"/>, never on the state merely being true —
/// otherwise a five-minute cron would re-raise the same finding twelve times an hour.
/// </summary>
public enum AlarmEvaluationState
{
    /// <summary>The metric is within the threshold.</summary>
    [Display(Name = "Normal")]
    Ok = 1,

    /// <summary>The metric is outside the threshold, on enough datapoints to count.</summary>
    [Display(Name = "In alarm")]
    Alarm = 2,

    /// <summary>Not enough data to judge — the alarm has just been created, the member has no
    /// established baseline for a baseline-relative threshold, or the window is empty.</summary>
    [Display(Name = "Not enough data")]
    InsufficientData = 3,
}
