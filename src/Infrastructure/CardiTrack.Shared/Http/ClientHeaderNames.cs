namespace CardiTrack.Shared.Http;

/// <summary>
/// Request headers identifying the build calling the API. The mobile app sends them on every
/// API call; the API copies them onto the request's span and log lines.
///
/// The names live here because the sender (CardiTrack.Mobile.Core) and the reader
/// (CardiTrack.API) share no other assembly — CardiTrack.Shared is the one project both
/// already reference, the same reason <see cref="Telemetry.TelemetryNames"/> sits beside it.
///
/// Deliberately not "X-ClientId": AspNetCoreRateLimit already claims that header as its
/// ClientIdHeader (see the API's appsettings.json), so reusing it would turn a version string
/// into a rate-limit bucket key.
/// </summary>
public static class ClientHeaderNames
{
    /// <summary>
    /// The calling build as "&lt;display version&gt;+&lt;build number&gt;" (e.g. "1.4.2+37") —
    /// MAUI's ApplicationDisplayVersion and ApplicationVersion, which CI stamps from the release
    /// tag and the commit count. The build number is what distinguishes two CI builds of the same
    /// display version.
    /// </summary>
    public const string ClientVersion = "X-Client-Version";

    /// <summary>
    /// The calling platform, lowercase — "android" or "ios" from a shipping build, "winui" from a
    /// developer's desktop run. Android/iOS parity is a hard product rule, so telemetry that can't
    /// separate the two can't answer whether a regression is one-sided.
    /// </summary>
    public const string ClientPlatform = "X-Client-Platform";
}
