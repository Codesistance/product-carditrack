namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// The values <see cref="AlertDetailResponse.Reason"/> takes. Named constants rather than an enum
/// because both ends of the wire are string-matching them — the mobile app switches on the value
/// to pick an icon, and an unrecognised one has to fall back rather than fail to deserialise.
/// </summary>
public static class AlertReasons
{
    public const string Activity = "activity";
    public const string Heart = "heart";
    public const string Sleep = "sleep";
    public const string Device = "device";

    /// <summary>The catch-all: something is off with this member's pattern, unattributed.</summary>
    public const string Monitoring = "monitoring";
}

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
    /// What the alert is about, as a stable key the mobile app maps to an icon: <c>activity</c>,
    /// <c>heart</c>, <c>sleep</c>, <c>device</c> or <c>monitoring</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not the severity. Severity is already carried by the banner's colour, so an
    /// icon spending itself on the same fact tells the caregiver nothing the screen had not
    /// already said — and the one thing the banner cannot say in colour is what kind of alert this
    /// is. Distinct from <see cref="Rule"/>, which is the producer's own stamp and too fine-grained
    /// to bind an icon to (five rules share three icons, and a new rule must not mean a new asset).
    /// </remarks>
    public string Reason { get; set; } = AlertReasons.Monitoring;

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

    /// <summary>
    /// The headline figure — always a <em>finished</em> day. Where the window runs up to the day
    /// in progress, that day is reported by <see cref="PartialDayLabel"/> instead, never here.
    /// </summary>
    public decimal? Value { get; set; }

    /// <summary>
    /// Which day <see cref="Value"/> belongs to ("Yesterday"), so the number in the chart header
    /// and the number in the comparison card are visibly the same number. Null when the window
    /// has no day in progress and the headline is simply the latest reading.
    /// </summary>
    public string? ValueLabel { get; set; }

    public decimal? Baseline { get; set; }
    public List<MetricPoint> Series { get; set; } = new();

    /// <summary>
    /// The day in progress, measured against the same stretch of the day before — "865 steps so
    /// far today, 21% below the 1,102 by this time yesterday".
    /// </summary>
    /// <remarks>
    /// A whole day against a part of one is not a comparison, and it is the comparison a caregiver
    /// makes on sight when a running total is plotted next to finished days. This sentence is the
    /// like-for-like one: both figures cover the same number of elapsed minutes since local
    /// midnight, summed from the minute-grain store. Null whenever that store cannot answer for
    /// either stretch — an unfair comparison is worse than none.
    /// </remarks>
    public string? PartialDayLabel { get; set; }
}
