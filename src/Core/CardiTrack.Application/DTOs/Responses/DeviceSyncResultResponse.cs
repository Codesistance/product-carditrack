namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// Outcome of a caregiver-triggered device sync (issue #67 — the dashboard refresh button).
/// </summary>
/// <remarks>
/// Reports per-device outcomes rather than a single pass/fail: a member can have more than one
/// connection, and one provider being down must not read as "nothing synced".
/// </remarks>
public class DeviceSyncResultResponse
{
    public int SyncedCount { get; set; }
    public int FailedCount { get; set; }

    /// <summary>Newest <c>LastSyncedAt</c> across the member's connections once the pull finished.</summary>
    public DateTime? LastSyncedAt { get; set; }

    public List<DeviceSyncOutcome> Devices { get; set; } = new();
}

public class DeviceSyncOutcome
{
    public Guid DeviceId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public bool Succeeded { get; set; }

    /// <summary>Why this device didn't sync. Null when it did.</summary>
    public string? Message { get; set; }
}
