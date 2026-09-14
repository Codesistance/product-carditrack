using System.Net.Http.Headers;
using CardiTrack.Mobile.Core.Http;
using CardiTrack.Shared.Http;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Which build, on which platform, is talking to the API — the values behind
/// <see cref="ClientHeaderNames"/> and the User-Agent. Read once from <c>AppInfo</c> and
/// <c>DeviceInfo</c>; the formatting lives in <see cref="ClientHeaders"/> so it can be tested
/// without a MAUI host.
/// </summary>
internal static class ClientIdentity
{
    /// <summary>"1.4.2+37", or null when the build carries no display version worth sending.</summary>
    public static string? Version =>
        ClientHeaders.FormatVersion(AppInfo.Current.VersionString, AppInfo.Current.BuildString);

    /// <summary>"android", "ios" or "winui" — lowercased so one platform never has two spellings.</summary>
    public static string? Platform =>
        ClientHeaders.NormalizePlatform(DeviceInfo.Current.Platform.ToString());

    /// <summary>
    /// Stamps every API call with which build is making it. The API turns these into tags on the
    /// request's server span and properties on every log line it writes, so a slow call or a 500
    /// can be attributed to an exact client build and platform instead of to "the mobile app".
    ///
    /// Default headers rather than a <c>DelegatingHandler</c>: both values are fixed for the
    /// process lifetime, so re-deriving them per request would buy nothing. Only clients that
    /// talk to our API get them — Auth0's host is not ours to describe our builds to.
    ///
    /// Either header is omitted rather than guessed at if its source value is missing or
    /// malformed; the API treats an absent header as "unknown", which is honest.
    /// </summary>
    public static void Apply(HttpRequestHeaders headers)
    {
        var version = Version;
        if (version is not null)
            headers.Add(ClientHeaderNames.ClientVersion, version);

        var platform = Platform;
        if (platform is not null)
            headers.Add(ClientHeaderNames.ClientPlatform, platform);

        // A User-Agent as well: HttpClient sends none by default, and at the edge an absent UA
        // is indistinguishable from an anonymous scanner — Cloud Armor's logs classed the app's
        // whole traffic as bot-like on exactly that (dev scan, 2026-08-20). The custom headers
        // above never leave our API's spans; the User-Agent is what the WAF and LB logs see.
        headers.UserAgent.Add(version is null
            ? new ProductInfoHeaderValue(new ProductHeaderValue("CardiTrack-Mobile"))
            : new ProductInfoHeaderValue("CardiTrack-Mobile", version));
        if (platform is not null)
            headers.UserAgent.Add(new ProductInfoHeaderValue($"({platform})"));
    }
}
