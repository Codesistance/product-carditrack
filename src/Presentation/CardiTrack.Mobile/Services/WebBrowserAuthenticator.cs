using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.Mobile.Services;

/// <summary>MAUI WebAuthenticator adapter for IBrowserAuthenticator — keeps Mobile.Core
/// MAUI-free. The carditrack://oauth scheme is registered on Android
/// (WebAuthenticationCallbackActivity) and iOS (Info.plist), shared with the Fitbit flow.</summary>
public sealed class WebBrowserAuthenticator(IAppResumeNotifier resumeNotifier) : IBrowserAuthenticator
{
    /// <summary>
    /// On Android, dismissing the Custom Tab without following the carditrack:// redirect (back
    /// button, swipe-away) doesn't reliably fault WebAuthenticator's task — see
    /// docs/apps/mobile/readme.md, "the deep link is the only thing that dismisses the in-app
    /// browser". The app resumes regardless (the Custom Tab closing brings MainActivity back to
    /// the foreground), so a resume is the only signal available that the sheet is gone. This is
    /// how long an already-in-flight deep link callback gets to land before a resume with no
    /// callback is treated as a cancel.
    /// </summary>
    private static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(1);

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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void OnResumed(object? sender, EventArgs e) => cts.CancelAfter(ResumeGrace);

        // The resume watchdog only fixes a bug that exists on Android (see the class remarks) —
        // wiring it on other platforms would risk cancelling a still-open native sheet (e.g. iOS's
        // ASWebAuthenticationSession) on a resume that has nothing to do with the sheet closing.
        if (OperatingSystem.IsAndroid())
            resumeNotifier.Resumed += OnResumed;

        try
        {
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
            var completed = await Task.WhenAny(authenticateTask, cancellationTask);
            if (completed == cancellationTask && !authenticateTask.IsCompleted)
                cts.Token.ThrowIfCancellationRequested();

            var result = await authenticateTask;
            AppForeground.BringToFront();
            return new Dictionary<string, string>(result.Properties, StringComparer.Ordinal);
        }
        finally
        {
            cts.Cancel();
            resumeNotifier.Resumed -= OnResumed;
        }
    }
}
