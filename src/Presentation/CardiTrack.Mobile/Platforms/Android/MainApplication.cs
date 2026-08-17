using Android.App;
using Android.Runtime;
using CardiTrack.Mobile.Notifications;
using Serilog;

namespace CardiTrack.Mobile.Platforms.Android;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>
    /// Creates the notification channels here, not only when
    /// <see cref="PushRegistrationCoordinator"/> is first resolved out of DI.
    /// </summary>
    /// <remarks>
    /// Android runs this for every process start, including the one it performs to hand an FCM
    /// message to <c>FirebaseMessagingService</c> — which is the case the coordinator's
    /// constructor cannot cover, because nothing resolves that singleton on a background
    /// delivery. Channels have to exist before the SDK posts, and their configuration is frozen
    /// at creation, so being late here is indistinguishable from being wrong forever.
    /// <para>
    /// Deliberately after <c>base.OnCreate()</c>: the base builds the MAUI app, which both
    /// establishes <see cref="global::Android.App.Application.Context"/> and runs
    /// <c>AppLogging.Configure</c>, so the context channel registration reads and the sink the
    /// catch below writes to are both live by this point. Wrapped because a throw here would
    /// take down every process start, push or not — a broken chime must not become a broken app.
    /// </para>
    /// <para>
    /// Serilog, not <c>Debug.WriteLine</c>: the latter is <c>[Conditional("DEBUG")]</c> and
    /// compiles to nothing in Release, which is the only build where this failing matters. A
    /// swallowed throw here leaves a permanently misconfigured channel and no evidence it ever
    /// happened — the same trap <c>MobileApm</c> documents.
    /// </para>
    /// </remarks>
    public override void OnCreate()
    {
        base.OnCreate();

        try
        {
            PushRegistrationCoordinator.RegisterAndroidChannels();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Notification channel registration failed at process start — this install " +
                          "may hold a misconfigured channel that no later run can repair.");
        }
    }
}
