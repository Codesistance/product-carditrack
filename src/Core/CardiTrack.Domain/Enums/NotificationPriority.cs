using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// Ranks notifications against each other for the inbox and the two dashboard card slots.
/// Deliberately separate from <see cref="AlertSeverity"/>: that describes a person's health,
/// this describes how badly the product needs a piece of information.
/// </summary>
public enum NotificationPriority
{
    [Display(Name = "Critical")]
    Critical = 1,

    [Display(Name = "High")]
    High = 2,

    [Display(Name = "Medium")]
    Medium = 3,

    [Display(Name = "Low")]
    Low = 4
}
