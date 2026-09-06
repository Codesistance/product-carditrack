using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// What the alarm's stored number means. The two baseline-relative kinds are resolved against the
/// member's <b>established 30-day</b> <see cref="Entities.PatternBaseline"/> only — a provisional
/// 7- or 14-day window never arms an alarm, exactly as it never fires a built-in rule.
/// </summary>
public enum AlarmThresholdKind
{
    /// <summary>The number is the threshold, in the metric's own unit. "Above 120 bpm."</summary>
    [Display(Name = "A fixed level")]
    Absolute = 1,

    /// <summary>The number is a percentage of the member's baseline average. 70 with a
    /// less-than operator reads "below 70% of their usual".</summary>
    [Display(Name = "A share of their usual")]
    BaselinePercent = 2,

    /// <summary>The number is a count of standard deviations from the member's baseline average,
    /// signed by the operator: a less-than operator puts the threshold below the mean, a
    /// greater-than operator above it. This is the form the built-in rules already use.</summary>
    [Display(Name = "A departure from their usual")]
    BaselineSigma = 3,
}
