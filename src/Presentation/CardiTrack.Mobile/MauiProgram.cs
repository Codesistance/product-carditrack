using System.Net.Http.Headers;
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
using CardiTrack.Shared.Http;
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
#endif

        var auth0 = new Auth0Options(AppConfig.Auth0Domain, AppConfig.Auth0ClientId, AppConfig.Auth0Audience);
        builder.Services.AddSingleton(auth0);
        builder.Services.AddSingleton(new ApiOptions(AppConfig.ApiBaseUrl));

        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton<ISecureKeyValueStore, SecureStorageKeyValueStore>();
        builder.Services.AddSingleton<IOfflineReadCache>(sp =>
            new EncryptedFileOfflineReadCache(
                Path.Combine(FileSystem.AppDataDirectory, "offline-cache"),
                sp.GetRequiredService<ISecureKeyValueStore>()));
        builder.Services.AddSingleton<IStatusLineStore, OfflineStatusLineStore>();
        builder.Services.AddSingleton<IDraftPhotoStore>(_ => new FileDraftPhotoStore(FileSystem.AppDataDirectory));
        builder.Services.AddSingleton<CardiMemberDraftStore>();
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
            AddClientIdentityHeaders(client.DefaultRequestHeaders);
            // Ceiling only — TimeoutHandler holds every request to 30 s unless the request
            // asked for more (the member-chat send does; its answer is a chain of CPU-served
            // model calls). HttpClient.Timeout can never be extended per request, so it must
            // sit above the slowest call and the handler enforces the real budgets.
            client.Timeout = TimeSpan.FromSeconds(190);
        })
        // Registered before the auth handler so a 401 refresh+retry spends the same request's
        // budget rather than getting a fresh one.
        .AddHttpMessageHandler(() => new TimeoutHandler(TimeSpan.FromSeconds(30)))
        .AddHttpMessageHandler<AuthHttpMessageHandler>();

        builder.Services.AddSingleton<IBrowserAuthenticator, WebBrowserAuthenticator>();
        builder.Services.AddSingleton<IPushDeviceRegistrationService, PushDeviceRegistrationService>();
        builder.Services.AddSingleton<IAuthService, AuthService>();
        builder.Services.AddSingleton<IPopupService, PopupService>();

        // Singleton so the "already reported" set outlives the pages that consult it — the member
        // detail page and the questions page both load the same pending question, and a lapsed one
        // should be reported to the server once, not once per screen that notices.
        builder.Services.AddSingleton<IQuestionValidityService, QuestionValidityService>();

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
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<NotificationPreferencesPage>();

        // Routed pages pushed over a tab (M1-11/12/16, M1-13 / M1-14 / M1-15).
        builder.Services.AddTransient<CardiMemberDetailPage>();
        builder.Services.AddTransient<EditCardiMemberPage>();
        builder.Services.AddTransient<DeviceManagementPage>();
        builder.Services.AddTransient<QuestionnairesPage>();
        builder.Services.AddTransient<MedicalInformationPage>();
        builder.Services.AddTransient<MetricTrendPage>();
        builder.Services.AddTransient<NotificationsPage>();
        builder.Services.AddTransient<AlertDetailPage>();

        var app = builder.Build();
        AppLogging.HookUnhandledExceptions(app.Services);
        return app;
    }

    /// <summary>
    /// Stamps every API call with which build is making it. The API turns these into tags on the
    /// request's server span and properties on every log line it writes, so a slow call or a 500
    /// can be attributed to an exact client build and platform instead of to "the mobile app".
    ///
    /// Default headers rather than a <c>DelegatingHandler</c>: both values are fixed for the
    /// process lifetime, so re-deriving them per request would buy nothing. Only the CardiTrack
    /// client gets them — Auth0's host is not ours to describe our builds to, and its client is
    /// deliberately outside our handler pipeline for the same reason.
    ///
    /// Either header is omitted rather than guessed at if its source value is missing or
    /// malformed; the API treats an absent header as "unknown", which is honest.
    /// </summary>
    private static void AddClientIdentityHeaders(HttpRequestHeaders headers)
    {
        var version = ClientHeaders.FormatVersion(AppInfo.Current.VersionString, AppInfo.Current.BuildString);
        if (version is not null)
            headers.Add(ClientHeaderNames.ClientVersion, version);

        // DevicePlatform is a struct whose ToString is the platform name ("Android", "iOS",
        // "WinUI"); ClientHeaders lowercases it so one platform can't appear under two spellings.
        var platform = ClientHeaders.NormalizePlatform(DeviceInfo.Current.Platform.ToString());
        if (platform is not null)
            headers.Add(ClientHeaderNames.ClientPlatform, platform);

        // A User-Agent as well: HttpClient sends none by default, and at the edge an absent UA
        // is indistinguishable from an anonymous scanner — Cloud Armor's logs classed the app's
        // whole traffic as bot-like on exactly that (dev scan, 2026-08-20). The custom headers
        // above never leave our API's spans; the User-Agent is what the WAF and LB logs see.
        headers.UserAgent.Add(version is null
            ? new ProductInfoHeaderValue(new ProductHeaderValue("CardiTrack-Mobile"))
            : new ProductInfoHeaderValue("CardiTrack-Mobile", version));
        if (platform is not null)
            headers.UserAgent.Add(new ProductInfoHeaderValue($"({platform})"));
    }
}
