using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// Why a notification left the open set. Drives the comply-rate metric, so the distinction
/// between "the user fixed it" and "it stopped applying" has to survive into reporting.
/// </summary>
public enum NotificationResolutionReason
{
    /// <summary>The user supplied what was missing. The only reason that counts as a comply.</summary>
    [Display(Name = "Gap Closed")]
    GapClosed = 1,

    /// <summary>The user chose "don't ask again"; a mute was written alongside.</summary>
    [Display(Name = "Dismissed")]
    Dismissed = 2,

    /// <summary>Monitoring was paused for the member the notification is about.</summary>
    [Display(Name = "Monitoring Paused")]
    MonitoringPaused = 3,

    /// <summary>The member or organization the notification belonged to went away.</summary>
    [Display(Name = "Scope Removed")]
    ScopeRemoved = 4
}
