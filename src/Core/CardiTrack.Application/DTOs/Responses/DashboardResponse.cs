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
    /// The number behind the dashboard's dedicated Emergency Call action (issue #67, reworked by
    /// issue #162 into its own visually distinct tile once <see cref="Phone"/> existed to power
    /// the plain "Call"/"Message" actions instead).
    /// </summary>
    public string? EmergencyContactPhone { get; set; }

    /// <summary>Who <see cref="EmergencyContactPhone"/> belongs to, so the UI can say.</summary>
    public string? EmergencyContactName { get; set; }

    /// <summary>
    /// The CardiMember's own phone, distinct from <see cref="EmergencyContactPhone"/> — behind
    /// the dashboard's "Call" and "Message" actions. Currently only captured on the Edit
    /// CardiMember screen (M1-14); onboarding (M1-04) does not collect it yet, so this is null
    /// for any member who hasn't since had it added. Same graceful-absence handling as the
    /// emergency contact.
    /// </summary>
    public string? Phone { get; set; }

    public string? PhotoUrl { get; set; }
    /// <summary>green/yellow/orange/red/unknown, or "paused" while monitoring is paused.</summary>
    public string HealthStatus { get; set; } = "unknown";

    public bool MonitoringPaused { get; set; }
    public DateTime? MonitoringPausedUntil { get; set; }
    public string? MonitoringPauseReason { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    /// <summary>
    /// Deterministic data-pipeline freshness, independent of <see cref="HealthStatus"/>'s clinical
    /// severity: red/amber = no sync in 12h/4h, blue = synced but not yet assessed, green = the
    /// latest sync has been assessed. Drives the CardiMember card's progress-bar caption.
    /// </summary>
    public string DataFreshness { get; set; } = "red";
    public string DataFreshnessMessage { get; set; } = string.Empty;

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

    /// <summary>
    /// Nightly skin temperature, not core body temperature — wrist wearables don't measure the
    /// latter. <see cref="DashboardMetric.Baseline"/> is the wearer's own nightly baseline from
    /// the device (<c>ActivityLog.TemperatureBaseline</c>), not a CardiTrack-computed pattern
    /// baseline, so this stays meaningful during the 30-day learning window.
    /// </summary>
    public DashboardMetric Temperature { get; set; } = new();

    /// <summary>Blood oxygen saturation. No established-baseline comparison exists for this
    /// metric yet, so <see cref="DashboardMetric.Status"/> stays "unknown" — the value is shown
    /// without a trend judgement.</summary>
    public DashboardMetric SpO2 { get; set; } = new();

    /// <summary>Breathing (respiratory) rate. Same no-established-baseline caveat as SpO2.</summary>
    public DashboardMetric BreathingRate { get; set; } = new();
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

    /// <summary>
    /// 1-5 star rating of the reading against this member's own normal — sleep efficiency where
    /// the device reports it, otherwise deviation from the baseline (see
    /// <c>MemberInsightsCalculator.RateAgainstNormal</c>). Null when there is nothing to rate
    /// against, which hides the card's star row rather than inventing a normal.
    /// </summary>
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
