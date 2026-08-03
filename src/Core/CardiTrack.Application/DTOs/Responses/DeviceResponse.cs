namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One connected wearable, per the REST contract in docs/execution/backend/api/devices.md.
/// Status is a lowercase string: active, disconnected, token_expired, pending.
/// </summary>
public class DeviceResponse
{
    public Guid DeviceId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? TokenExpiresAt { get; set; }
}

public class DeviceListResponse
{
    public List<DeviceResponse> Devices { get; set; } = new();
}
