namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One caregiver-requested history re-pull, as the device list and the request endpoint report
/// it (M1-15). <see cref="Status"/> is a lowercase wire string: <c>pending</c>,
/// <c>in_progress</c>, <c>completed</c>, <c>failed</c>, <c>cancelled</c>.
/// </summary>
public class DeviceHistoryRepullResponse
{
    public Guid RepullId { get; set; }

    public string Status { get; set; } = string.Empty;

    /// <summary>How many days the request covers — what the caregiver asked for.</summary>
    public int Days { get; set; }

    public DateOnly FromDate { get; set; }

    public DateOnly ToDate { get; set; }

    /// <summary>Days fetched so far, counting from the newest. Equals <see cref="Days"/> when complete.</summary>
    public int DaysDone { get; set; }

    /// <summary>Of the days fetched, how many the provider had data for.</summary>
    public int DaysWithData { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// When this connection may be re-pulled again, or null when it may be re-pulled now (or
    /// once the open request finishes). Set only on a completed request inside its cooldown.
    /// </summary>
    public DateTime? NextAllowedAt { get; set; }
}
