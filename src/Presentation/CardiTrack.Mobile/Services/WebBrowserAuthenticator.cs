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
        var authenticateTask = WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
        {
            Url = authorizeUri,
            CallbackUrl = callbackUri,
        });

        // WebAuthenticator has no cancellation overload — it can't be told to close the
        // browser sheet. Racing the await against the token at least stops this call
        // from hanging its caller; the in-flight browser session ends when the user
        // dismisses it or the callback arrives, whichever the token doesn't preempt.
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var completed = await Task.WhenAny(authenticateTask, cancellationTask);
        if (completed == cancellationTask)
            ct.ThrowIfCancellationRequested();

        var result = await authenticateTask;
        return new Dictionary<string, string>(result.Properties, StringComparer.Ordinal);
    }
}
