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

    /// <summary>
    /// What the grant is for: <see cref="ModeAdd"/> (the default when omitted),
    /// <see cref="ModeReconnect"/> or <see cref="ModeReplace"/>. The latter two name the
    /// connection they act on in <see cref="DeviceId"/>.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>The connection to reconnect or replace; must be absent when adding.</summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// A device alongside the member's others. A grant for an account the member already has
    /// connected refreshes that connection instead of storing the same data twice.
    /// </summary>
    public const string ModeAdd = "add";

    /// <summary>Re-authorise <see cref="DeviceId"/> on the same provider account.</summary>
    public const string ModeReconnect = "reconnect";

    /// <summary>
    /// Connect a device in place of <see cref="DeviceId"/>: the new connection takes over its
    /// primary flag and the old one is removed, once — and only once — the new grant is stored.
    /// </summary>
    public const string ModeReplace = "replace";
}
