using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

public enum DevicePlatform
{
    [Display(Name = "iOS")]
    Ios = 1,

    [Display(Name = "Android")]
    Android = 2
}
