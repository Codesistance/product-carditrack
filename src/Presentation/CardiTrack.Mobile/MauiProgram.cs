using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Configuration;
using CardiTrack.Mobile.Core.Http;
using CardiTrack.Mobile.Services;
#if ANDROID || IOS
using Datadog.Maui;
using Datadog.Maui.Configuration;
using Datadog.Maui.Hosting;
#endif

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

#if ANDROID || IOS
        // Datadog RUM: crash reporting + API request monitoring. The client token is
        // embed-safe (write-only) and stamped by CI — unstamped builds ship nothing.
        // Session Replay is deliberately NOT enabled: health data must not be recorded.
        if (AppConfig.IsDatadogConfigured)
        {
            builder
                .UseDatadog(new DdSdkConfiguration
                {
                    ClientToken = AppConfig.DatadogClientToken,
                    Environment = AppConfig.EnvironmentName,
                    TrackingConsent = TrackingConsent.Granted,
                    Service = "carditrack-mobile",
                    Site = Enum.TryParse<DatadogSite>(AppConfig.DatadogSite, ignoreCase: true, out var site)
                        ? site
                        : DatadogSite.Eu1,
                    NativeCrashReportEnabled = true,
                    // Marks our API as first-party so RUM resources correlate with the
                    // API's OTel traces (RUM-to-APM), via W3C traceparent headers.
                    FirstPartyHosts =
                    [
                        new FirstPartyHost
                        {
                            Match = new Uri(AppConfig.ApiBaseUrl).Host,
                            HeaderTypes = [TracingHeaderType.Datadog, TracingHeaderType.TraceContext],
                        },
                    ],
                })
                .UseDatadogLogs()
                .UseDatadogRum(new DdRumConfiguration
                {
                    ApplicationId = AppConfig.DatadogRumApplicationId,
                    SessionSampleRate = 100.0,
                });
        }
#endif

        var auth0 = new Auth0Options(AppConfig.Auth0Domain, AppConfig.Auth0ClientId, AppConfig.Auth0Audience);
        builder.Services.AddSingleton(auth0);
        builder.Services.AddSingleton(new ApiOptions(AppConfig.ApiBaseUrl));

        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
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
        builder.Services.AddSingleton<PostLoginRouter>();

        // Shell tab pages resolve through DI (constructor injection).
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<AlertsPage>();
        builder.Services.AddTransient<FamilyPage>();
        builder.Services.AddTransient<SettingsPage>();

        var app = builder.Build();
        AppLogging.HookUnhandledExceptions(app.Services);
        return app;
    }
}
