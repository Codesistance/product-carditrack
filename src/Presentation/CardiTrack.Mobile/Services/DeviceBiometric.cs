using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Platform biometric unlock. Android uses the OS biometric prompt; other
/// targets report unavailable so export falls back to the password popup.
/// </summary>
public sealed class DeviceBiometric : IDeviceBiometric
{
#if ANDROID
    public bool IsAvailable
    {
        get
        {
            var context = Android.App.Application.Context;
            var manager = (Android.Hardware.Biometrics.BiometricManager)
                context.GetSystemService(Android.Content.Context.BiometricService)!;
            var authenticators =
                Android.Hardware.Biometrics.BiometricManager.Authenticators.BiometricStrong
                | Android.Hardware.Biometrics.BiometricManager.Authenticators.BiometricWeak;
            return manager.CanAuthenticate(authenticators)
                == Android.Hardware.Biometrics.BiometricManager.BiometricSuccess;
        }
    }

    public Task<bool> AuthenticateAsync(string reason, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                    as AndroidX.AppCompat.App.AppCompatActivity;
                if (activity is null)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                var authenticators =
                    Android.Hardware.Biometrics.BiometricManager.Authenticators.BiometricStrong
                    | Android.Hardware.Biometrics.BiometricManager.Authenticators.BiometricWeak;

                var info = new Android.Hardware.Biometrics.BiometricPrompt.PromptInfo.Builder()
                    .SetTitle("Confirm it's you")
                    .SetSubtitle(reason)
                    .SetNegativeButtonText("Cancel")
                    .SetAllowedAuthenticators(authenticators)
                    .Build();

                var callback = new PromptCallback(tcs);
                var executor = AndroidX.Core.Content.ContextCompat.GetMainExecutor(activity);
                var prompt = new Android.Hardware.Biometrics.BiometricPrompt(activity, executor, callback);
                prompt.Authenticate(info);
            }
            catch (Exception)
            {
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    private sealed class PromptCallback : Android.Hardware.Biometrics.BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> _tcs;

        public PromptCallback(TaskCompletionSource<bool> tcs) => _tcs = tcs;

        public override void OnAuthenticationSucceeded(
            Android.Hardware.Biometrics.BiometricPrompt.AuthenticationResult result) =>
            _tcs.TrySetResult(true);

        public override void OnAuthenticationFailed()
        {
            // The prompt stays open for another try. Completing here would treat
            // "not this finger" as a finished refusal while the sheet is still up.
        }

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence? errString) =>
            _tcs.TrySetResult(false);
    }
#else
    public bool IsAvailable => false;

    public Task<bool> AuthenticateAsync(string reason, CancellationToken ct = default) =>
        Task.FromResult(false);
#endif
}
