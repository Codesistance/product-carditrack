using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Interfaces;

namespace CardiTrack.Domain.Entities;

public class Alert : BaseEntity, ISoftDeletable
{
    public Guid CardiMemberId { get; set; }
    public AlertType AlertType { get; set; }
    public AlertSeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime TriggeredDate { get; set; }
    public DateTime? AcknowledgedDate { get; set; }
    public Guid? AcknowledgedByUserId { get; set; } // User who acknowledged the alert
    public bool IsResolved { get; set; }

    /// <summary>
    /// The caregiver who closed it, or null when CardiTrack resolved it because the condition
    /// passed.
    /// </summary>
    /// <remarks>
    /// The distinction is worth a column. "This cleared up on its own" and "Jane checked and it
    /// was nothing" are different facts about the same resolved alert, and a screen that cannot
    /// tell them apart either credits the product for a person's work or credits a person for the
    /// product's. Null is the pre-existing meaning of every row written before caregivers could
    /// close anything, which is also the correct one: nobody closed them.
    /// </remarks>
    public Guid? ResolvedByUserId { get; set; }

    // JSON: { "steps": 450, "baseline": 5200, "deviation": -91.3 }
    public string? MetricValues { get; set; }

    public bool IsActive { get; set; } = true;

    public Alert()
    {
        TriggeredDate = DateTime.UtcNow;
    }
}
