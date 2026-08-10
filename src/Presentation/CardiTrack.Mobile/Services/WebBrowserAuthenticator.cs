using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.Mobile.Services;

/// <summary>MAUI WebAuthenticator adapter for IBrowserAuthenticator — keeps Mobile.Core
/// MAUI-free. The carditrack://oauth scheme is registered on Android
/// (WebAuthenticationCallbackActivity) and iOS (Info.plist), shared with the Fitbit flow.</summary>
public sealed class WebBrowserAuthenticator : IBrowserAuthenticator
{
    public async Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(
        Uri authorizeUri, Uri callbackUri, CancellationToken ct = default)
    {
        var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
        {
            Url = authorizeUri,
            CallbackUrl = callbackUri,
        });
        return new Dictionary<string, string>(result.Properties, StringComparer.Ordinal);
    }
}
