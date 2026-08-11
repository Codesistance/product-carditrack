using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// OS-level push permission state (notification_engine.md §4). Superset of iOS's
/// <c>UNAuthorizationStatus</c>; Android only ever reports <see cref="Granted"/> or
/// <see cref="Denied"/>. Read on every foreground and reported with token registration —
/// a caregiver who silently turned notifications off is indistinguishable from one who is
/// being reached, unless the app checks.
/// </summary>
public enum OsAuthorizationStatus
{
    [Display(Name = "Not determined")]
    NotDetermined = 1,

    [Display(Name = "Denied")]
    Denied = 2,

    [Display(Name = "Granted")]
    Granted = 3,

    /// <summary>iOS-only: quiet delivery to Notification Center without a prompt, used for the nudge channel.</summary>
    [Display(Name = "Provisional")]
    Provisional = 4,

    /// <summary>iOS-only: App Clip-scoped, time-limited authorization. Not expected in this app, modelled for completeness.</summary>
    [Display(Name = "Ephemeral")]
    Ephemeral = 5
}
