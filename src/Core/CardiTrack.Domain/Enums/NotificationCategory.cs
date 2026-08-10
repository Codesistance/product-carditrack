using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// What kind of gap a <see cref="Entities.Notification"/> is about. Category — not priority —
/// decides whether the user may silence it and, from R2, whether it may leave the app.
/// </summary>
public enum NotificationCategory
{
    /// <summary>
    /// Monitoring is degraded or nobody is listening. Cannot be muted; snooze is capped and
    /// dismissal records an acknowledged consequence.
    /// </summary>
    [Display(Name = "Safety")]
    Safety = 1,

    /// <summary>Core value is unavailable until the gap closes — no device, stalled baseline.</summary>
    [Display(Name = "Blocking")]
    Blocking = 2,

    /// <summary>A capability the user could unlock by supplying something.</summary>
    [Display(Name = "Unlock")]
    Unlock = 3,

    /// <summary>Account and subscription lifecycle.</summary>
    [Display(Name = "Account")]
    Account = 4
}
