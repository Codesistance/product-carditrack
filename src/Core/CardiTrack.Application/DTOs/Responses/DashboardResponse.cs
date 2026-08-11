namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// Composed payload for the mobile Main Dashboard (M1-09): hero status, key metrics with
/// 7-day series, recent alerts, and device/baseline state in a single round-trip.
/// Status/severity fields are lowercase strings (green/yellow/orange/red/unknown) per the
/// REST contract in docs/execution/backend/api.
/// </summary>
public class DashboardResponse
{
    public Guid CardiMemberId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }

    /// <summary>
    /// The number behind the dashboard's Call and Send Message actions (issue #67).
    /// </summary>
    /// <remarks>
    /// Deliberately the emergency contact rather than <c>CardiMember.Phone</c>: the emergency
    /// contact is the only phone number any screen actually captures (M1-04 / M1-14), so
    /// <c>Phone</c> is null for every member created in the app and shipping the quick actions
    /// against it would leave them permanently dead.
    /// </remarks>
    public string? EmergencyContactPhone { get; set; }

    /// <summary>Who <see cref="EmergencyContactPhone"/> belongs to, so the UI can say.</summary>
    public string? EmergencyContactName { get; set; }

    /// <summary>
    /// The CardiMember's own phone, distinct from <see cref="EmergencyContactPhone"/> — captured
    /// on the Add/Edit CardiMember screens. Null for any member who hasn't been given one, same
    /// graceful-absence handling as the emergency contact.
    /// </summary>
    public string? Phone { get; set; }

    public string? PhotoUrl { get; set; }
    /// <summary>green/yellow/orange/red/unknown, or "paused" while monitoring is paused.</summary>
    public string HealthStatus { get; set; } = "unknown";

    public bool MonitoringPaused { get; set; }
    public DateTime? MonitoringPausedUntil { get; set; }
    public string? MonitoringPauseReason { get; set; }

    public DateTime? LastSyncedAt { get; set; }
    public int UnreadAlertCount { get; set; }
    public DashboardDeviceState Device { get; set; } = new();
    public DashboardBaselineState Baseline { get; set; } = new();
    public DashboardMetrics? Metrics { get; set; }
    public List<DashboardAlertSummary> RecentAlerts { get; set; } = new();
    public DateTime GeneratedAt { get; set; }
}

public class DashboardDeviceState
{
    public bool HasActiveConnection { get; set; }
    public string? DeviceType { get; set; }
    public string? DeviceName { get; set; }
    public string? ConnectionStatus { get; set; }
    public DateTime? LastSyncDate { get; set; }
}

public class DashboardBaselineState
{
    public bool IsLearning { get; set; }

    /// <summary>
    /// True while the metrics are coloured against a baseline shorter than
    /// <see cref="DaysRequired"/> days — an early impression, not an established normal.
    /// Clients should caveat comparisons accordingly.
    /// </summary>
    public bool IsProvisional { get; set; }

    /// <summary>Window (in days) of the baseline in use; null while still learning.</summary>
    public int? BaselinePeriodDays { get; set; }

    public int DaysCaptured { get; set; }
    public int DaysRequired { get; set; } = 30;
    public int PercentComplete { get; set; }
}

public class DashboardMetrics
{
    public DashboardMetric Steps { get; set; } = new();
    public DashboardMetric RestingHeartRate { get; set; } = new();
    public DashboardMetric Sleep { get; set; } = new();
}

public class DashboardMetric
{
    public decimal? Value { get; set; }
    public decimal? Baseline { get; set; }
    public decimal? ChangePercent { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    public decimal? Goal { get; set; }
    public int? RangeLow { get; set; }
    public int? RangeHigh { get; set; }
    public int? QualityScore { get; set; }
    public List<MetricPoint> Series { get; set; } = new();
}

public class MetricPoint
{
    public DateOnly Date { get; set; }
    public decimal? Value { get; set; }
}

public class DashboardAlertSummary
{
    public Guid AlertId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Severity { get; set; } = "yellow";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime TriggeredAt { get; set; }
    public bool IsAcknowledged { get; set; }
}
