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
    /// Extra authorize-URL query parameters, e.g. Google's access_type=offline (without which no
    /// refresh token is issued) and prompt=consent.
    /// </summary>
    public Dictionary<string, string> AdditionalAuthorizationParams { get; set; } = [];

    /// <summary>Access token lifetime in hours. Used to compute TokenExpiry on storage.</summary>
    public int TokenLifetimeHours { get; set; } = 8;
}
