using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// The escalation ladder for Safety and red Health deliveries (notification_engine.md §6.3):
/// push every device, then re-push at t+120s, then fan out to other caregivers at t+300s, then
/// page ops at t+900s. In R1 (<c>MaxUsers = 1</c>) <see cref="FannedOut"/> is reachable but has
/// no secondary caregivers to notify — the ladder still runs it, rather than special-casing the
/// step away, so it is not silently removed when family invitations (R3) add real targets.
/// </summary>
public enum EscalationStage
{
    [Display(Name = "Initial")]
    Initial = 1,

    [Display(Name = "Re-pushed")]
    Repushed = 2,

    [Display(Name = "Fanned out")]
    FannedOut = 3,

    [Display(Name = "Undelivered — critical")]
    UndeliveredCritical = 4
}
