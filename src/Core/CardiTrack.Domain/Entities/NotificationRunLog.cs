using CardiTrack.Domain.Common;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One row per detection run. A rule that starts misfiring shows up here as a spike in
/// <see cref="Created"/> before any user complains, and a crashed run leaves
/// <see cref="LastOrganizationId"/> behind so the next one can pick up where it stopped.
/// </summary>
public class NotificationRunLog : BaseEntity
{
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Last organization fully processed — the resume point after a crash.</summary>
    public Guid? LastOrganizationId { get; set; }

    public int OrganizationsScanned { get; set; }
    public int Created { get; set; }
    public int Resolved { get; set; }
    public int Suppressed { get; set; }

    public int DurationMs { get; set; }

    /// <summary>Set when the run ended badly. Null on a clean finish.</summary>
    public string? Error { get; set; }

    public NotificationRunLog()
    {
        StartedAt = DateTime.UtcNow;
    }
}
