namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Initiates a server-OAuth device connection (POST /api/v1/cardimembers/{id}/devices).
/// </summary>
public class ConnectDeviceRequest
{
    /// <summary>Server-OAuth provider name per the REST contract: fitbit, garmin, samsung_health, withings.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Deep link the provider redirects back to after authorization (e.g. carditrack://oauth/callback).</summary>
    public string RedirectUri { get; set; } = string.Empty;
}
