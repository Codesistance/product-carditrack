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

    /// <summary>
    /// Local time of day this member's Daybook is written, or null for
    /// <see cref="Common.JournalSchedule.DefaultLocalTime"/>. Held on the member rather than on a
    /// caregiver because a book is written once for the member and read by everyone who cares for
    /// them — two caregivers cannot each have their own copy of the same finished day, so this is
    /// a property of whose day it is, not of who is reading.
    /// </summary>
    public TimeOnly? DaybookLocalTime { get; set; }

    /// <summary>
    /// Local time of day this member's Weekbook is written on <see cref="JournalWeekStartsOn"/>,
    /// or null for the default. Stored ahead of the generator that reads it (R2).
    /// </summary>
    public TimeOnly? WeekbookLocalTime { get; set; }

    /// <summary>
    /// Local time of day this member's Monthbook is written on the 1st, or null for the default.
    /// Stored ahead of the generator that reads it (R2).
    /// </summary>
    public TimeOnly? MonthbookLocalTime { get; set; }

    /// <summary>
    /// The weekday this member's CardiJournal week begins, or null for
    /// <see cref="Common.JournalSchedule.DefaultWeekStartsOn"/>. A Weekbook covers the seven days
    /// ending the evening before this day, and is written on it.
    /// </summary>
    public DayOfWeek? JournalWeekStartsOn { get; set; }

    /// <summary>
    /// How far this member's bedtime must move before a book calls it earlier or later, or null
    /// for <see cref="Common.JournalComparison.DefaultBedtimeToleranceMinutes"/>. On the member
    /// for the reason the book timings are: the distance that counts as a change is a fact about
    /// whose nights these are, not about who is reading the account.
    /// </summary>
    public int? DaybookBedtimeToleranceMinutes { get; set; }

    /// <inheritdoc cref="DaybookBedtimeToleranceMinutes"/>
    public int? DaybookWakeToleranceMinutes { get; set; }

    /// <summary>
    /// How far apart two clock times must be before a book stops naming a direction for them at
    /// all, or null for the default. See
    /// <see cref="Common.JournalComparison.DefaultDirectionBoundMinutes"/>.
    /// </summary>
    public int? DaybookDirectionBoundMinutes { get; set; }

    /// <summary>
    /// The band around a numeric usual, as a percentage of it, inside which a book calls the
    /// reading level rather than above or below — or null for the default. Widens the resolution
    /// floor each reading's own format already imposes; it can never narrow it.
    /// </summary>
    public decimal? DaybookLevelTolerancePercent { get; set; }

    /// <summary>Paused state expires on its own — callers must never treat the pause as sticky.</summary>
    public bool IsMonitoringPaused(DateTime utcNow) => MonitoringPausedUntil > utcNow;

    // Navigation properties
    public ICollection<UserCardiMember> UserCardiMembers { get; set; } = new List<UserCardiMember>();
    public ICollection<DeviceConnection> DeviceConnections { get; set; } = new List<DeviceConnection>();
    public ICollection<ActivityLog> ActivityLogs { get; set; } = new List<ActivityLog>();
    public ICollection<Alert> Alerts { get; set; } = new List<Alert>();
    public ICollection<PatternBaseline> PatternBaselines { get; set; } = new List<PatternBaseline>();
}
