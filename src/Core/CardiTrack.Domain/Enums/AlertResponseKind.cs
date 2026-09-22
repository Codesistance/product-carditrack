using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// What a caregiver's answer to an alert was: taking it on, or ending it.
/// </summary>
public enum AlertResponseKind
{
    /// <summary>
    /// Somebody has this. Stops the escalation ladder chasing the family, and leaves the alert
    /// open — the condition has not been said to have passed, only that it is being dealt with.
    /// </summary>
    [Display(Name = "Acknowledged")]
    Acknowledge = 1,

    /// <summary>
    /// It is dealt with. Resolves the alert, which also re-arms the rule — so a condition that
    /// persists raises a fresh alert rather than staying silent behind somebody's note.
    /// </summary>
    [Display(Name = "Closed")]
    Close = 2
}
