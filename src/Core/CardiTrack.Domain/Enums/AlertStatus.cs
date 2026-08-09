using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// Where an alert sits in its lifecycle: <c>New</c> → <c>Acknowledged</c> → <c>Resolved</c>.
/// </summary>
/// <remarks>
/// Derived rather than stored. <see cref="Entities.Alert"/> records the two facts the lifecycle
/// is read from — <c>AcknowledgedDate</c> and <c>IsResolved</c> — and every projection maps them
/// the same way, so there is no third column that could disagree with them.
/// </remarks>
public enum AlertStatus
{
    [Display(Name = "New")]
    New = 1,

    [Display(Name = "Acknowledged")]
    Acknowledged = 2,

    [Display(Name = "Resolved")]
    Resolved = 3
}
