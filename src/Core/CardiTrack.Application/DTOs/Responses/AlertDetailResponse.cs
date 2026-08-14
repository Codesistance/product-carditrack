namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One alert for the mobile detail screen (M1-11 / M1-12 / M1-16). The chart, when present, is
/// the single series that caused the alert — never the dashboard's six-metric payload.
/// </summary>
public class AlertDetailResponse
{
    public Guid AlertId { get; set; }
    public Guid CardiMemberId { get; set; }
    public string CardiMemberName { get; set; } = string.Empty;
    public string? CardiMemberPhotoUrl { get; set; }
    public string? Phone { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? EmergencyContactName { get; set; }

    /// <summary>AlertType display name — "Inactivity", "Heart Rate", …</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// The producer stamp in <c>MetricValues</c> (<c>activity_decline</c>, <c>realtime_hr</c>, …),
    /// or null when the row predates rule markers.
    /// </summary>
    public string? Rule { get; set; }

    /// <summary>green/yellow/orange/red.</summary>
    public string Severity { get; set; } = "yellow";

    /// <summary>new/acknowledged/resolved.</summary>
    public string Status { get; set; } = "new";

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime TriggeredAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public string? AcknowledgedByName { get; set; }

    /// <summary>The two-column "current vs usual" block, or null when the rule has no scalars.</summary>
    public AlertComparisonResponse? Comparison { get; set; }

    /// <summary>
    /// The one series this alert is about. Null when the rule has no health graph
    /// (<c>device_silence</c>) or there is nothing to plot.
    /// </summary>
    public AlertChartResponse? Chart { get; set; }

    /// <summary>Last day with measured steps, for no-morning / silence copy. Null when unknown.</summary>
    public DateOnly? LastActivityOn { get; set; }

    /// <summary>Typical wake time ("07:00") for the no-morning rule.</summary>
    public string? TypicalWakeTime { get; set; }

    /// <summary>When the device last produced a reading, for <c>device_silence</c>.</summary>
    public DateTime? LastDataAt { get; set; }
}

public class AlertComparisonResponse
{
    public string CurrentLabel { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    public string NormalLabel { get; set; } = string.Empty;
    public string NormalValue { get; set; } = string.Empty;
    public string? ChangeLabel { get; set; }
}

/// <summary>
/// One metric's window for the detail chart. <see cref="Series"/> is oldest-first; a missing
/// day (or minute) is a point with a null value, not a hole in the list.
/// </summary>
public class AlertChartResponse
{
    /// <summary>steps / restingHeartRate / sleep / heartRate.</summary>
    public string Metric { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;

    /// <summary>Caption under the name — "Last 14 days", "This hour".</summary>
    public string WindowLabel { get; set; } = string.Empty;

    public decimal? Value { get; set; }
    public decimal? Baseline { get; set; }
    public List<MetricPoint> Series { get; set; } = new();
}
