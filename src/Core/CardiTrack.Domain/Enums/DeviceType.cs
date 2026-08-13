using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// The hardware brand on the wearer's wrist. Which <see cref="HealthApi"/> a brand syncs through
/// is a configuration mapping (several brands can share one API), so adding a brand served by an
/// already-integrated API is an enum member + config entry, not a new engine.
/// </summary>
public enum DeviceType
{
    [Display(Name = "Fitbit")]
    Fitbit = 1,

    [Display(Name = "Apple Watch")]
    AppleWatch = 2,

    [Display(Name = "Garmin")]
    Garmin = 3,

    [Display(Name = "Samsung Galaxy Watch")]
    GalaxyWatch = 4,

    [Display(Name = "Withings")]
    Withings = 5,

    [Display(Name = "Oura")]
    Oura = 6,

    [Display(Name = "Whoop")]
    Whoop = 7,

    [Display(Name = "Google Pixel Watch")]
    GooglePixelWatch = 8,

    [Display(Name = "Other")]
    Other = 99
}
