namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Initiates a server-OAuth device connection (POST /api/v1/cardimembers/{id}/devices).
/// </summary>
public class ConnectDeviceRequest
{
    /// <summary>
    /// The only scheme a device callback may come back on. The anonymous bounce endpoint
    /// forwards into whatever was cached here, so allowing another scheme would make it an
    /// open redirect leaking code+state.
    /// </summary>
    public const string AppRedirectScheme = "carditrack";

    /// <summary>Server-OAuth provider name per the REST contract: fitbit, pixel_watch, garmin, samsung_health, withings.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Deep link the provider redirects back to after authorization (e.g. carditrack://oauth/callback).</summary>
    public string RedirectUri { get; set; } = string.Empty;
}
