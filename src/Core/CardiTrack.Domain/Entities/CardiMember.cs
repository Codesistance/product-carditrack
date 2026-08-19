using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Interfaces;

namespace CardiTrack.Domain.Entities;

public class CardiMember : BaseEntity, ISoftDeletable
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateOnly DateOfBirth { get; set; }
    public Gender Gender { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? MedicalNotes { get; set; } // Encrypted at rest — see CardiMemberService
    public DateTime? LastSyncDate { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When monitoring resumes, or null when the member is being monitored normally (M1-13).
    /// A time-bounded pause rather than a flag so a caregiver can never leave someone
    /// silently unmonitored by forgetting to switch it back on.
    /// </summary>
    public DateTime? MonitoringPausedUntil { get; set; }

    /// <summary>Optional caregiver-supplied reason shown to the rest of the family.</summary>
    public string? MonitoringPauseReason { get; set; }

    public AlertSensitivity AlertSensitivity { get; set; } = AlertSensitivity.Medium;

    /// <summary>
    /// Object name of this member's profile photo in the private member-photos bucket
    /// (<c>members/{id}/{guid}.jpg</c>), or null when no photo has been set. The object name,
    /// never a URL: read surfaces mint short-lived signed URLs on demand, so nothing durable
    /// carries a fetchable link to a full-face photo (Safe Harbor category 17 — see
    /// docs/technical/data_protection_architecture.md). The blob is deleted when the photo is
    /// replaced or removed and when the member is removed — it must not outlive the membership.
    /// </summary>
    public string? PhotoObjectName { get; set; }

    /// <summary>
    /// Whether this member's exercise-session GPS location may be used to look up ambient
    /// temperature and air quality for that session. Default false, and the sole gate: no code
    /// path fetches exercise/location data or calls the environmental client for a member whose
    /// flag is not set. Raw coordinates are never persisted regardless — only the derived
    /// readings this consent unlocks (see docs/technical/data_protection_architecture.md).
    /// </summary>
    public bool EnvironmentalContextConsentGranted { get; set; }

    /// <summary>Paused state expires on its own — callers must never treat the pause as sticky.</summary>
    public bool IsMonitoringPaused(DateTime utcNow) => MonitoringPausedUntil > utcNow;

    // Navigation properties
    public ICollection<UserCardiMember> UserCardiMembers { get; set; } = new List<UserCardiMember>();
    public ICollection<DeviceConnection> DeviceConnections { get; set; } = new List<DeviceConnection>();
    public ICollection<ActivityLog> ActivityLogs { get; set; } = new List<ActivityLog>();
    public ICollection<Alert> Alerts { get; set; } = new List<Alert>();
    public ICollection<PatternBaseline> PatternBaselines { get; set; } = new List<PatternBaseline>();
}
