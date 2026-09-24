using CardiTrack.Mobile.Core.Alerts;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Configuration;
using CardiTrack.Mobile.Core.Http;
using CardiTrack.Mobile.Core.Media;
using CardiTrack.Mobile.Core.Notifications;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Core.Questionnaires;
using CardiTrack.Mobile.Services;
#if ANDROID || IOS
using CardiTrack.Mobile.Notifications;
using Microsoft.Maui.LifecycleEvents;
using Plugin.Firebase.CloudMessaging;
#endif
#if ANDROID
using Plugin.Firebase.Core.Platforms.Android;
using AndroidCrossFirebase = Plugin.Firebase.Core.Platforms.Android.CrossFirebase;
#elif IOS
using AppleCrossFirebase = Plugin.Firebase.Core.Platforms.iOS.CrossFirebase;
#endif

namespace CardiTrack.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // First statement in the app's managed lifetime: SplashPage measures its minimum
        // brand hold from here, so anything ahead of it inflates that hold.
        AppStartup.Mark();

        AppConfig.Validate();

        var builder = MauiApp.CreateBuilder();
        AppLogging.Configure(builder.Logging);
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("Quicksand.ttf", "Quicksand");
                fonts.AddFont("Quicksand-Medium.ttf", "QuicksandMedium");
                fonts.AddFont("Quicksand-SemiBold.ttf", "QuicksandSemiBold");
            });

        // Crash/session monitoring — engine + data stamped by CI; unstamped builds ship nothing.
        MobileApm.Configure(builder);

        // The fields draw their own edge (AuthEntryBorder); the platform's underline was a second one.
        Controls.FieldChrome.RemoveNativeUnderline();

        // Autofill belongs to this account's own credentials, not to a CardiMember's details —
        // off everywhere but the fields that ask for it.
        Controls.FieldAutofill.ApplyPolicy();

        // Push delivery spine (notification_engine.md Phase 3). No Windows support — the two
        // Cloud Run env vars this needs (Notifications__AckTokenKey etc.) are server-side only;
        // there is nothing platform-specific to configure here on that target, so it is simply
        // absent from these #if blocks rather than special-cased.
#if ANDROID || IOS
        builder.ConfigureLifecycleEvents(events =>
        {
#if IOS
            events.AddiOS(ios => ios.WillFinishLaunching((app, launchOptions) =>
            {
                AppleCrossFirebase.Initialize();
                FirebaseCloudMessagingImplementation.Initialize();
                return true;
            }));
#elif ANDROID
            events.AddAndroid(android => android.OnCreate((activity, _) =>
                AndroidCrossFirebase.Initialize(activity, () => Platform.CurrentActivity!)));
#endif
        });

        builder.Services.AddSingleton(_ => CrossFirebaseCloudMessaging.Current);
        builder.Services.AddSingleton<PushRegistrationCoordinator>();
        builder.Services.AddSingleton<IPendingNavigation>(sp =>
            sp.GetRequiredService<PushRegistrationCoordinator>());
#endif

        var auth0 = new Auth0Options(AppConfig.Auth0Domain, AppConfig.Auth0ClientId, AppConfig.Auth0Audience);
        builder.Services.AddSingleton(auth0);
        builder.Services.AddSingleton(new ApiOptions(AppConfig.ApiBaseUrl));

        builder.Services.AddSingleton<SessionGeneration>();

        // Shared by every API client the container builds: the typed client is transient and the
        // offline cache is not, so the order cached writes land in has to be kept somewhere they
        // all see — see CacheWriteOrder.
        builder.Services.AddSingleton<CacheWriteOrder>();
        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton<ISecureKeyValueStore, SecureStorageKeyValueStore>();
        builder.Services.AddSingleton<IOfflineReadCache>(sp =>
            new EncryptedFileOfflineReadCache(
                Path.Combine(FileSystem.AppDataDirectory, "offline-cache"),
                sp.GetRequiredService<ISecureKeyValueStore>()));
        builder.Services.AddSingleton<IStatusLineStore, OfflineStatusLineStore>();
        builder.Services.AddSingleton<IDraftPhotoStore>(_ => new FileDraftPhotoStore(FileSystem.AppDataDirectory));
        builder.Services.AddSingleton<CardiMemberDraftStore>();
        builder.Services.AddSingleton<AlertResponseDraftStore>();
        builder.Services.AddSingleton<IProfilePhotoTranscoder, MauiProfilePhotoTranscoder>();
        builder.Services.AddSingleton<ITokenRefresher, TokenRefresher>();
        builder.Services.AddTransient<AuthHttpMessageHandler>();

        // Auth0 client deliberately has NO auth handler — login/refresh calls must not
        // recurse through the bearer pipeline.
        builder.Services.AddHttpClient<IAuth0AuthClient, Auth0AuthClient>(client =>
        {
            if (auth0.IsConfigured)
                client.BaseAddress = new Uri($"https://{auth0.Domain}");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        builder.Services.AddHttpClient<ICardiTrackApiClient, CardiTrackApiClient>(client =>
        {
            client.BaseAddress = new Uri(AppConfig.ApiBaseUrl);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            ClientIdentity.Apply(client.DefaultRequestHeaders);
            // Ceiling only — TimeoutHandler holds every request to 30 s unless the request
            // asked for more (the member-chat send does; its answer is a chain of model calls).
            // HttpClient.Timeout can never be extended per request, so it must sit above the
            // slowest call and the handler enforces the real budgets.
            client.Timeout = CardiTrackApiClient.HttpClientCeiling;
        })
        // Registered before the auth handler so a 401 refresh+retry spends the same request's
        // budget rather than getting a fresh one.
        .AddHttpMessageHandler(() => new TimeoutHandler(TimeSpan.FromSeconds(30)))
        .AddHttpMessageHandler<AuthHttpMessageHandler>();

        builder.Services.AddSingleton<IBrowserAuthenticator, WebBrowserAuthenticator>();
        builder.Services.AddSingleton<IPushDeviceRegistrationService, PushDeviceRegistrationService>();
        builder.Services.AddSingleton<IOfflineCacheWarmer, OfflineCacheWarmer>();
        builder.Services.AddSingleton<IAuthService, AuthService>();
        builder.Services.AddSingleton<IDeviceBiometric, DeviceBiometric>();
        builder.Services.AddSingleton<IPopupService, PopupService>();
        builder.Services.AddSingleton<IExportConsentFlow, ExportConsentFlow>();
        builder.Services.AddSingleton<IExportFileSaver, ExportFileSaver>();
        builder.Services.AddSingleton<IExportFileDelivery, ExportFileDelivery>();
        builder.Services.AddSingleton<IJournalExportFlow, JournalExportFlow>();
        builder.Services.AddSingleton<IChatTranscriptExportFlow, ChatTranscriptExportFlow>();

        // Singleton so the "already reported" set outlives the pages that consult it — the member
        // detail page and the questions page both load the same pending question, and a lapsed one
        // should be reported to the server once, not once per screen that notices.
        builder.Services.AddSingleton<IQuestionValidityService, QuestionValidityService>();

        // Singleton because outliving the page is the point: CardiMemberDetailPage is transient
        // behind a registered route, so Shell builds a new one on every navigation and anything
        // it remembers about how recently it read the generated cards goes with the old one.
        builder.Services.AddSingleton<IGeneratedContentSchedule, GeneratedContentSchedule>();

        // One instance behind both types: App raises the foreground signal on the concrete
        // class, pages listen through the interface.
        builder.Services.AddSingleton<AppResumeNotifier>();
        builder.Services.AddSingleton<IAppResumeNotifier>(sp => sp.GetRequiredService<AppResumeNotifier>());
        builder.Services.AddSingleton<PostLoginRouter>();

        // Shell tab pages resolve through DI (constructor injection).
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<AlertsPage>();
        builder.Services.AddTransient<JournalPage>();
        builder.Services.AddTransient<JournalEntryPage>();
        builder.Services.AddTransient<JournalTimingPage>();
        builder.Services.AddTransient<AlertSettingsPage>();
        builder.Services.AddTransient<MetricAlarmsPage>();
        builder.Services.AddTransient<MetricAlarmEditPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<NotificationPreferencesPage>();
        builder.Services.AddTransient<ExportConsentsPage>();

        // Routed pages pushed over a tab (M1-11/12/16, M1-13 / M1-14 / M1-15).
        builder.Services.AddTransient<CardiMemberDetailPage>();
        builder.Services.AddTransient<EditCardiMemberPage>();
        builder.Services.AddTransient<DeviceManagementPage>();
        builder.Services.AddTransient<ExportHealthDataPage>();
        builder.Services.AddTransient<QuestionnairesPage>();
        builder.Services.AddTransient<MedicalInformationPage>();
        builder.Services.AddTransient<MetricTrendPage>();
        builder.Services.AddTransient<NotificationsPage>();
        builder.Services.AddTransient<AlertDetailPage>();
        builder.Services.AddTransient<AlertRespondPage>();
        builder.Services.AddTransient<FamilyPage>();
        builder.Services.AddTransient<StartFamilyPage>();
        builder.Services.AddTransient<JoinFamilyPage>();
        builder.Services.AddTransient<AcceptInvitePage>();
        builder.Services.AddTransient<ApproveJoinRequestPage>();
        builder.Services.AddTransient<TransferFamilyAdminPage>();
        builder.Services.AddTransient<CaregiverInvitesPage>();

        var app = builder.Build();
        AppLogging.HookUnhandledExceptions(app.Services);
        // Whatever the last run queued — above all an unhandled exception it aborted on —
        // goes to the API now, before anything else can crash this run.
        AppLogging.FlushDiagnostics();
#if ANDROID || IOS
        // Constructing the coordinator is what subscribes its FCM handlers. AppShell used to
        // be the first resolver, so a push that woke a killed process reached
        // FirebaseMessagingService with nobody listening — the ack never posted and the
        // cache never warmed. Resolving it here, as soon as the app exists, covers that wake.
        try
        {
            _ = app.Services.GetRequiredService<PushRegistrationCoordinator>();
        }
        catch (Exception)
        {
            // Firebase initialisation is the realistic failure (a missing google-services
            // file in a local build). Push not working must not cost the rest of the app.
        }
#endif
        return app;
    }
}
