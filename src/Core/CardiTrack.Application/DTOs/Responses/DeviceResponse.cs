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

    /// <summary>Granted data scopes, e.g. ["activity", "heartrate", "sleep"] (M1-15).</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// When the sync worker will next pick this connection up. Derived from the last sync and
    /// the connection's own interval, so it is an estimate, not a scheduled job time.
    /// </summary>
    public DateTime? NextSyncAt { get; set; }

    /// <summary>How many of today's activity records came from this device (M1-15).</summary>
    public int TodayUpdateCount { get; set; }

    /// <summary>
    /// Last reported battery percentage (0–100), or null when there is nothing trustworthy to
    /// show — the connection predates the <c>settings</c> scope, the device reports only a band,
    /// the hardware has no battery, or the reading has aged past
    /// <see cref="Services.DeviceBattery.FreshFor"/>. Clients render the tile only when this or
    /// <see cref="BatteryStatus"/> is present; null is a normal state, not an error.
    /// </summary>
    public int? BatteryLevel { get; set; }

    /// <summary>
    /// The provider's coarse battery band — <c>High</c>, <c>Medium</c>, <c>Low</c> or
    /// <c>Empty</c>. Present for devices that report a band but no percentage, and null under the
    /// same conditions as <see cref="BatteryLevel"/>.
    /// </summary>
    public string? BatteryStatus { get; set; }
}

public class DeviceListResponse
{
    public List<DeviceResponse> Devices { get; set; } = new();
}
