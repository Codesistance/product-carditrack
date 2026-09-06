using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>How the datapoint is compared against the alarm's effective threshold.</summary>
public enum AlarmOperator
{
    [Display(Name = "is above")]
    GreaterThan = 1,

    [Display(Name = "is at or above")]
    GreaterThanOrEqualTo = 2,

    [Display(Name = "is below")]
    LessThan = 3,

    [Display(Name = "is at or below")]
    LessThanOrEqualTo = 4,
}
