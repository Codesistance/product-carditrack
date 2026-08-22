using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

public enum AlertType
{
    [Display(Name = "Inactivity")]
    Inactivity = 1,

    [Display(Name = "Heart Rate")]
    HeartRate = 2,

    [Display(Name = "Sleep")]
    Sleep = 3,

    [Display(Name = "Pattern Break")]
    PatternBreak = 4,

    [Display(Name = "Trend")]
    Trend = 5
}
