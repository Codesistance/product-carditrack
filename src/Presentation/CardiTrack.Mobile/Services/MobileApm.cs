using CardiTrack.Shared.Json;
using Newtonsoft.Json.Linq;
#if ANDROID || IOS
using Datadog.Maui;
using Datadog.Maui.Configuration;
using Datadog.Maui.Hosting;
#endif

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Mobile twin of the server's ApmProviderRegistry: AppConfig.ApmEngine names an engine
/// here, AppConfig.ApmData carries that engine's client-side connection JSON (embed-safe
/// identifiers only — never runtime secrets). Unlike the server, a bad engine name or
/// malformed data logs and skips instead of failing: a monitoring misconfiguration must
/// never brick the app. Everything is a no-op on platforms an engine doesn't support.
/// </summary>
public static class MobileApm
{
    private static readonly Dictionary<string, Action<MauiAppBuilder, JObject>> Engines =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Datadog"] = ConfigureDatadog,
        };

    public static void Configure(MauiAppBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(AppConfig.ApmEngine) || string.IsNullOrWhiteSpace(AppConfig.ApmData))
            return;

        if (!Engines.TryGetValue(AppConfig.ApmEngine, out var configure))
        {
            System.Diagnostics.Debug.WriteLine(
                $"MobileApm: unknown engine '{AppConfig.ApmEngine}' (known: {string.Join(", ", Engines.Keys)}) — monitoring disabled.");
            return;
        }

        if (!JsonUtility.TryParse(AppConfig.ApmData, out var data, out var errors) || data is not JObject payload)
        {
            System.Diagnostics.Debug.WriteLine(
                $"MobileApm: ApmData is not a JSON object — monitoring disabled. {string.Join("; ", errors)}");
            return;
        }

        configure(builder, payload);
    }

    /// <summary>
    /// Datadog RUM: crash reporting + session tracking + API request timing.
    /// Data: {"ClientToken":"pub...","ApplicationId":"...","Site":"Eu1"} — client token
    /// and application id are write-only identifiers, safe to embed.
    /// Session Replay is deliberately NOT enabled: health data must not be recorded.
    /// </summary>
    private static void ConfigureDatadog(MauiAppBuilder builder, JObject data)
    {
#if ANDROID || IOS
        var clientToken = data.Value<string>("ClientToken");
        var applicationId = data.Value<string>("ApplicationId");
        if (string.IsNullOrWhiteSpace(clientToken) || string.IsNullOrWhiteSpace(applicationId))
        {
            System.Diagnostics.Debug.WriteLine(
                "MobileApm: Datadog data needs ClientToken and ApplicationId — monitoring disabled.");
            return;
        }

        builder
            .UseDatadog(new DdSdkConfiguration
            {
                ClientToken = clientToken,
                Environment = AppConfig.EnvironmentName,
                TrackingConsent = TrackingConsent.Granted,
                Service = "carditrack-mobile",
                Site = Enum.TryParse<DatadogSite>(data.Value<string>("Site"), ignoreCase: true, out var site)
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
                ApplicationId = applicationId,
                SessionSampleRate = 100.0,
            });
#endif
    }
}
