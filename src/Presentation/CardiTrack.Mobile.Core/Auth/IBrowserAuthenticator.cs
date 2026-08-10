namespace CardiTrack.Mobile.Core.Auth;

/// <summary>
/// System-browser OAuth round-trip. Implemented over MAUI's WebAuthenticator in
/// CardiTrack.Mobile (Services/WebBrowserAuthenticator) so Mobile.Core stays MAUI-free —
/// same split as ITokenStore / SecureTokenStore.
/// </summary>
public interface IBrowserAuthenticator
{
    /// <returns>Query parameters of the callback deep link.</returns>
    /// <exception cref="TaskCanceledException">The user dismissed the browser sheet.</exception>
    /// <exception cref="PlatformNotSupportedException">No system-browser auth on this platform (Windows).</exception>
    Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(
        Uri authorizeUri, Uri callbackUri, CancellationToken ct = default);
}
