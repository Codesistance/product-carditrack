using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// Everything the mobile CardiMember Detail screen (M1-13) renders, in one round trip.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="CardiMemberResponse"/>: this payload carries the
/// emergency contact and medical notes, so it is only ever served for an explicitly
/// requested member. Folding these fields into the list response would broadcast one
/// member's PHI on every "which members do I have?" call.
/// </remarks>
public class CardiMemberDetailResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public int Age { get; set; }
    public Gender Gender { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public RelationshipType Relationship { get; set; }
    public bool IsPrimaryCaregiver { get; set; }

    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? MedicalNotes { get; set; }

    /// <summary>Null — no photo storage exists yet; clients fall back to an initials avatar.</summary>
    public string? PhotoUrl { get; set; }

    public AlertSensitivity AlertSensitivity { get; set; }

    public bool MonitoringPaused { get; set; }
    public DateTime? MonitoringPausedUntil { get; set; }
    public string? MonitoringPauseReason { get; set; }

    /// <summary>When this member was added — the screen's "Monitoring Since".</summary>
    public DateTime MonitoringSince { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public int ConnectedDeviceCount { get; set; }

    public DashboardBaselineState Baseline { get; set; } = new();

    /// <summary>green/yellow/orange/red/unknown — same field and meaning as
    /// <see cref="DashboardResponse.HealthStatus"/>, computed the same way
    /// (<see cref="CardiTrack.Application.Services.MemberInsightsCalculator.ComputeHealthStatus"/>).
    /// Accents the daily summary card by severity.</summary>
    public string HealthStatus { get; set; } = "unknown";

    /// <summary>Key Metrics with their daily series, for this screen's trend cards — see
    /// <see cref="DashboardMetric.Series"/> for how long a series runs and which window a client
    /// showing fewer days takes. Null when there's no activity history yet, same condition
    /// <see cref="DashboardResponse.Metrics"/> uses.</summary>
    public DashboardMetrics? Metrics { get; set; }
}
