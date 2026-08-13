#if ANDROID || IOS
using CardiTrack.Mobile.Core.Notifications;
using CardiTrack.Mobile.Notifications;
using CardiTrack.Mobile.Services;
using Microsoft.Extensions.Logging;
#endif

namespace CardiTrack.Mobile;

public partial class AppShell : Shell
{
    /// <summary>
    /// Absolute routes for the tabs declared in AppShell.xaml. A control that names where it
    /// is going — "Go to Dashboard" — navigates to one of these: the route selects the tab and
    /// drops whatever sits above it, so the user arrives at the page the button promised. ".."
    /// and modal pops only unwind one step of history, which lands wherever the user happened
    /// to come from and is only the dashboard by coincidence.
    /// </summary>
    public const string DashboardRoute = "//dashboard";

    public const string AlertsRoute = "//alerts";

    public const string FamilyRoute = "//family";

    public const string SettingsRoute = "//settings";

    public AppShell()
    {
        InitializeComponent();

        // Pages pushed on top of a tab rather than owning one. Registered here so
        // GoToAsync("<route>?memberId=...") resolves them through DI.
        Routing.RegisterRoute(CardiMemberDetailPage.Route, typeof(CardiMemberDetailPage));
        Routing.RegisterRoute(EditCardiMemberPage.Route, typeof(EditCardiMemberPage));
        Routing.RegisterRoute(DeviceManagementPage.Route, typeof(DeviceManagementPage));
        Routing.RegisterRoute(QuestionnairesPage.Route, typeof(QuestionnairesPage));
        Routing.RegisterRoute(NotificationsPage.Route, typeof(NotificationsPage));

#if ANDROID || IOS
        WirePush();
#endif
    }

#if ANDROID || IOS
    /// <summary>
    /// Reaching the Shell at all means onboarding finished, and onboarding cannot finish without a
    /// device connection succeeding — so §4's "permission at the moment of value, not on launch"
    /// has already been satisfied by the time this runs. That makes this the one place an install
    /// which onboarded <em>before</em> push shipped can still be asked: <c>ConnectionSuccessPage</c>
    /// is behind such a caregiver forever, so it was the only caller and they never hit it.
    /// </summary>
    /// <remarks>
    /// Resolving the coordinator is load-bearing beyond registration: it is a singleton, and
    /// constructing it is what subscribes its FCM handlers. Until something asked for it, an
    /// arriving push was never delivered-acked (so the escalation ladder ran on regardless) and a
    /// tapped one never navigated — the outcome its own <c>DestinationTapped</c> doc comment
    /// already described AppShell as providing.
    /// </remarks>
    private void WirePush()
    {
        PushRegistrationCoordinator push;
        try
        {
            push = ServiceHelper.GetRequiredService<PushRegistrationCoordinator>();
        }
        catch (Exception ex)
        {
            // Firebase initialisation is the realistic failure here (a missing google-services
            // plist/json in a local build). Push not working must not cost the caregiver the
            // whole app shell.
            LogWarning(ex, "Push coordinator unavailable — notifications will not arrive.");
            return;
        }

        push.DestinationTapped += OnPushDestinationTapped;

        // Every foreground, not just this one: it picks up a rotated FCM token, and it is the
        // LastSeenDate heartbeat that NotificationDispatchWorker's staleness sweep treats as
        // proof the install is still alive. Loaded/Unloaded rather than the constructor alone,
        // so a shell discarded on sign-out stops holding the singleton notifier's event.
        var notifier = ServiceHelper.GetRequiredService<IAppResumeNotifier>();
        void OnResumed(object? sender, EventArgs e) => RegisterPush(push);

        Loaded += (_, _) =>
        {
            notifier.Resumed -= OnResumed;
            notifier.Resumed += OnResumed;
        };
        Unloaded += (_, _) =>
        {
            notifier.Resumed -= OnResumed;
            push.DestinationTapped -= OnPushDestinationTapped;
        };

        RegisterPush(push);
    }

    /// <summary>
    /// Fire-and-forget by design — the coordinator swallows its own failures (a denied permission
    /// just arms PUSH_UNREACHABLE server-side next time reachability is evaluated), and nothing on
    /// screen waits for a token.
    /// </summary>
    private static void RegisterPush(PushRegistrationCoordinator push) =>
        _ = push.RequestPermissionAndRegisterAsync();

    private void OnPushDestinationTapped(object? sender, NudgeDestination destination)
    {
        if (DeepLinkRouter.Resolve(destination) is not { } route)
            return;

        // The tap is raised from the platform's notification callback, which is not guaranteed to
        // be the UI thread; Shell navigation requires it.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await GoToAsync(route);
            }
            catch (Exception ex)
            {
                // Reached from a fire-and-forget handler: a route that no longer resolves must
                // not take the app down on the way in from a notification.
                LogWarning(ex, $"Navigating to '{route}' from a tapped notification failed.");
            }
        });
    }

    private static void LogWarning(Exception ex, string message)
    {
        try
        {
            ServiceHelper.GetRequiredService<ILogger<AppShell>>().LogWarning(ex, message);
        }
        catch
        {
            // Nothing left to report it with.
        }
    }
#endif
}
