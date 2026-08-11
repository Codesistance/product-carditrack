using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Upserts a device's push token (POST /api/v1/notifications/devices). Doubles as the
/// reachability heartbeat — <see cref="OsAuthorizationStatus"/>/<see cref="SafetyChannelEnabled"/>
/// are read on every foreground and sent along with re-registration (notification_engine.md §4).
/// </summary>
public class RegisterPushDeviceRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public DevicePlatform Platform { get; set; }
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>The raw FCM registration token. Never logged, never stored except encrypted.</summary>
    public string Token { get; set; } = string.Empty;

    public OsAuthorizationStatus OsAuthorizationStatus { get; set; }
    public bool SafetyChannelEnabled { get; set; } = true;
}

/// <summary>Unregisters a device (DELETE /api/v1/notifications/devices).</summary>
public class UnregisterPushDeviceRequest
{
    public string DeviceId { get; set; } = string.Empty;
}
