using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Configuration;
using CardiTrack.Mobile.Core.Http;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        AppConfig.Validate();

        var builder = MauiApp.CreateBuilder();
        AppLogging.Configure(builder.Logging);
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("Quicksand.ttf", "Quicksand");
                fonts.AddFont("Quicksand-Medium.ttf", "QuicksandMedium");
                fonts.AddFont("Quicksand-SemiBold.ttf", "QuicksandSemiBold");
                fonts.AddFont("Manrope-SemiBold.ttf", "ManropeSemiBold");
            });

        // Crash/session monitoring — engine + data stamped by CI; unstamped builds ship nothing.
        MobileApm.Configure(builder);

        var auth0 = new Auth0Options(AppConfig.Auth0Domain, AppConfig.Auth0ClientId, AppConfig.Auth0Audience);
        builder.Services.AddSingleton(auth0);
        builder.Services.AddSingleton(new ApiOptions(AppConfig.ApiBaseUrl));

        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton<ISecureKeyValueStore, SecureStorageKeyValueStore>();
        builder.Services.AddSingleton<IDraftPhotoStore>(_ => new FileDraftPhotoStore(FileSystem.AppDataDirectory));
        builder.Services.AddSingleton<CardiMemberDraftStore>();
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
            client.Timeout = TimeSpan.FromSeconds(30);
        }).AddHttpMessageHandler<AuthHttpMessageHandler>();

        builder.Services.AddSingleton<IAuthService, AuthService>();
        builder.Services.AddSingleton<IPopupService, PopupService>();
        builder.Services.AddSingleton<PostLoginRouter>();

        // Shell tab pages resolve through DI (constructor injection).
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<AlertsPage>();
        builder.Services.AddTransient<FamilyPage>();
        builder.Services.AddTransient<SettingsPage>();

        // Routed pages pushed over a tab (M1-13 / M1-14 / M1-15).
        builder.Services.AddTransient<CardiMemberDetailPage>();
        builder.Services.AddTransient<EditCardiMemberPage>();
        builder.Services.AddTransient<DeviceManagementPage>();

        var app = builder.Build();
        AppLogging.HookUnhandledExceptions(app.Services);
        return app;
    }
}
