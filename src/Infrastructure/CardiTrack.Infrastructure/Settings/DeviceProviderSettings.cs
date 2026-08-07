namespace CardiTrack.Infrastructure.Settings;

public class DeviceProviderSettings
{
    public const string SectionName = "DeviceProviders";

    /// <summary>Matches DeviceType enum name (e.g. "Fitbit", "Garmin").</summary>
    public string Provider { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AuthorizationUrl { get; set; } = string.Empty;
    public string TokenUrl { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = [];

    /// <summary>
    /// Provider-facing redirect URI. When set (e.g. Google requires an https redirect for web
    /// clients), it is used in the authorize URL and code exchange instead of the app deep link;
    /// the API's oauth/redirect endpoint then bounces the browser back to the deep link.
    /// </summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// Extra authorize-URL query parameters sent on every authorization, e.g. Google's
    /// access_type=offline (without which no refresh token is issued).
    /// </summary>
    public Dictionary<string, string> AdditionalAuthorizationParams { get; set; } = [];

    /// <summary>
    /// Authorize-URL parameters added only while we hold no refresh token for the member on this
    /// provider — Google's prompt=consent belongs here. Google re-issues a refresh token only when
    /// consent is shown again, but sending it unconditionally makes the user re-approve a device
    /// they have already approved on every reconnect.
    /// </summary>
    public Dictionary<string, string> FirstConsentAuthorizationParams { get; set; } = [];

    /// <summary>Access token lifetime in hours. Used to compute TokenExpiry on storage.</summary>
    public int TokenLifetimeHours { get; set; } = 8;
}
