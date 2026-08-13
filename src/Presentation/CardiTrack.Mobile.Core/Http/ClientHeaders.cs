using CardiTrack.Shared.Http;

namespace CardiTrack.Mobile.Core.Http;

/// <summary>
/// Builds the values for the <see cref="ClientHeaderNames"/> headers the app sends to the API.
///
/// This lives in Mobile.Core, not in the MAUI head, purely so it can be tested: the inputs come
/// from <c>AppInfo</c> and <c>DeviceInfo</c>, which only exist in a MAUI app host, so MauiProgram
/// reads them and passes plain strings here. The formatting is the part that can be wrong; the
/// header assignment that follows it cannot.
/// </summary>
public static class ClientHeaders
{
    /// <summary>
    /// Joins the display version and build number into the wire form, "1.4.2+37". Returns null
    /// when there is nothing trustworthy to send — no display version, or a value that fails
    /// <see cref="ClientHeaderValues.IsValid"/>.
    ///
    /// Sending nothing beats sending something malformed: the API would reject it anyway, and an
    /// absent header reads as "this build predates the header" rather than as a real value.
    /// </summary>
    public static string? FormatVersion(string? displayVersion, string? buildNumber)
    {
        var version = displayVersion?.Trim();
        if (string.IsNullOrEmpty(version))
            return null;

        // The build number is the optional half. A build with a display version but no build
        // number still identifies itself usefully, just not down to the individual CI run.
        var build = buildNumber?.Trim();
        var value = string.IsNullOrEmpty(build) ? version : $"{version}+{build}";

        return ClientHeaderValues.IsValid(value) ? value : null;
    }

    /// <summary>
    /// Normalises MAUI's <c>DevicePlatform</c> name ("Android", "iOS", "WinUI") to the lowercase
    /// form the API records. Case is folded here rather than at the reader so that "Android" and
    /// "android" can never appear as two different values in the same APM query.
    /// </summary>
    public static string? NormalizePlatform(string? platform)
    {
        var value = platform?.Trim().ToLowerInvariant();
        return ClientHeaderValues.IsValid(value) ? value : null;
    }
}
