using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Platform biometric unlock. Android uses the OS biometric prompt; other
/// targets report unavailable so export falls back to the password popup.
/// </summary>
public sealed class DeviceBiometric : IDeviceBiometric
{
#if ANDROID
    private const Android.Hardware.Biometrics.BiometricManagerAuthenticators Allowed =
        Android.Hardware.Biometrics.BiometricManagerAuthenticators.BiometricStrong
        | Android.Hardware.Biometrics.BiometricManagerAuthenticators.BiometricWeak;

    public bool IsAvailable
    {
        get
        {
            try
            {
                var context = Android.App.Application.Context;
                if (context.GetSystemService(Android.Content.Context.BiometricService)
                    is not Android.Hardware.Biometrics.BiometricManager manager)
                    return false;

                return manager.CanAuthenticate((int)Allowed)
                    == Android.Hardware.Biometrics.BiometricCode.Success;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public Task<bool> AuthenticateAsync(string reason, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            tcs.TrySetResult(false);
            return tcs.Task;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            PromptCallback? callback = null;
            try
            {
                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                var executor = activity?.MainExecutor;
                if (activity is null || executor is null)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                callback = new PromptCallback(tcs);

                // The prompt refuses to build without a negative button unless
                // device credential is an allowed authenticator, which it isn't.
                var prompt = new Android.Hardware.Biometrics.BiometricPrompt.Builder(activity)
                    .SetTitle("Confirm it's you")
                    .SetSubtitle(reason)
                    .SetAllowedAuthenticators((int)Allowed)
                    .SetNegativeButton("Cancel", executor, new CancelListener(callback))
                    .Build();

                var signal = new Android.OS.CancellationSignal();
                callback.Attach(signal, ct.Register(() =>
                {
                    try
                    {
                        signal.Cancel();
                    }
                    catch (Exception)
                    {
                        // Prompt already dismissed.
                    }

                    callback.Complete(false);
                }));

                prompt.Authenticate(signal, executor, callback);
            }
            catch (Exception)
            {
                callback?.Complete(false);
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    private sealed class CancelListener :
        Java.Lang.Object, Android.Content.IDialogInterfaceOnClickListener
    {
        private readonly PromptCallback _callback;

        public CancelListener(PromptCallback callback) => _callback = callback;

        public void OnClick(Android.Content.IDialogInterface? dialog, int which) =>
            _callback.Complete(false);
    }

    private sealed class PromptCallback : Android.Hardware.Biometrics.BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> _tcs;
        private Android.OS.CancellationSignal? _signal;
        private CancellationTokenRegistration _registration;

        public PromptCallback(TaskCompletionSource<bool> tcs) => _tcs = tcs;

        public void Attach(Android.OS.CancellationSignal signal, CancellationTokenRegistration registration)
        {
            _signal = signal;
            _registration = registration;

            // A token cancelled before Register returns runs Complete without
            // these; tidy them up here instead of leaking the signal.
            if (_tcs.Task.IsCompleted)
                Complete(false);
        }

        public void Complete(bool value)
        {
            _registration.Dispose();
            _signal?.Dispose();
            _signal = null;
            _tcs.TrySetResult(value);
        }

        public override void OnAuthenticationSucceeded(
            Android.Hardware.Biometrics.BiometricPrompt.AuthenticationResult? result) =>
            Complete(true);

        public override void OnAuthenticationFailed()
        {
            // The prompt stays open for another try. Completing here would treat
            // "not this finger" as a finished refusal while the sheet is still up.
        }

        // errorCode is enumified by the binding; an int parameter overrides nothing.
        public override void OnAuthenticationError(
            Android.Hardware.Biometrics.BiometricErrorCode errorCode,
            Java.Lang.ICharSequence? errString) =>
            Complete(false);
    }
#else
    public bool IsAvailable => false;

    public Task<bool> AuthenticateAsync(string reason, CancellationToken ct = default)
    {
        _ = reason;
        _ = ct;
        return Task.FromResult(false);
    }
#endif
}
