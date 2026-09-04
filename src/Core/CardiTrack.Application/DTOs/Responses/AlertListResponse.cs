namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// Payload for the mobile Alerts List (M1-10): one page of alerts across every CardiMember the
/// caller may read, plus the counts the list header and tab badge need.
/// Type/severity/status fields are lowercase strings per the REST contract in
/// docs/execution/backend/api/alerts.md.
/// </summary>
public class AlertListResponse
{
    public List<AlertSummaryResponse> Alerts { get; set; } = new();

    /// <summary>How many alerts match the filters, ignoring paging — drives "load more".</summary>
    public int Total { get; set; }

    /// <summary>
    /// Unacknowledged, unresolved alerts across the queried members, deliberately
    /// <em>ignoring</em> the other filters: it is the badge count, so it must not change
    /// when the caregiver switches to the "Critical" chip.
    /// </summary>
    public int UnreadCount { get; set; }
}

public class AlertSummaryResponse
{
    public Guid AlertId { get; set; }
    public Guid CardiMemberId { get; set; }
    public string CardiMemberName { get; set; } = string.Empty;
    public string? CardiMemberPhotoUrl { get; set; }

    /// <summary>
    /// The number behind the card's Call action. The emergency contact rather than
    /// <c>CardiMember.Phone</c>, for the same reason the dashboard uses it — see
    /// <see cref="DashboardResponse.EmergencyContactPhone"/>.
    /// </summary>
    public string? EmergencyContactPhone { get; set; }
    public string? EmergencyContactName { get; set; }

    /// <summary>
    /// The CardiMember's own phone, behind the card's Call action — so the card's two phone
    /// actions mean what the dashboard's do: Call reaches the member, SOS reaches
    /// <see cref="EmergencyContactPhone"/>. Null when they have no number on file.
    /// </summary>
    public string? CardiMemberPhone { get; set; }

    /// <summary>AlertType display name — "Inactivity", "Heart Rate", "Sleep", …</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>green/yellow/orange/red.</summary>
    public string Severity { get; set; } = "yellow";

    /// <summary>new/acknowledged/resolved.</summary>
    public string Status { get; set; } = "new";

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime TriggeredAt { get; set; }

    /// <summary>
    /// The civil day the alert is <em>about</em> — yesterday's quiet day for
    /// <c>activity_decline</c>, not the instant the worker raised it. List grouping (Today /
    /// Yesterday) keys off this so a next-day statistical alert does not land under Today.
    /// </summary>
    public DateOnly AboutDate { get; set; }

    public DateTime? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
}

/// <summary>Result of acknowledging one alert — enough for the card to re-render in place.</summary>
public class AlertAcknowledgementResponse
{
    public Guid AlertId { get; set; }
    public string Status { get; set; } = "acknowledged";
    public DateTime? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }

    /// <summary>The queried scope's unread count after the change, so the badge stays honest.</summary>
    public int UnreadCount { get; set; }
}
