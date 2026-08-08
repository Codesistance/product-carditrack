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
    /// Data: {"ClientToken":"pub...","ApplicationId":"...","Site":"Uk1",
    /// "CustomEndpoint":"https://browser-intake-uk1-datadoghq.com"} — client token and
    /// application id are write-only identifiers, safe to embed. CustomEndpoint is
    /// optional; it is only needed for sites Datadog.Maui cannot name (see below).
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

        var customEndpoint = data.Value<string>("CustomEndpoint");
        var siteName = data.Value<string>("Site");

        // Datadog.Maui 0.2.0's DatadogSite enum names only Us1/Us3/Us5/Eu1/Ap1/Ap2/Us1Fed,
        // while the native SDKs it wraps reach more sites (Uk1 among them). A site the enum
        // cannot name is addressed instead by pointing the Logs and RUM features at that
        // site's intake host via CustomEndpoint. Without one we would quietly ship this
        // app's telemetry to whatever the fallback resolved to — a different org in a
        // different region — so refuse to start monitoring at all. Omitting Site entirely
        // keeps the documented Eu1 default.
        if (!Enum.TryParse<DatadogSite>(siteName, ignoreCase: true, out var site))
        {
            site = DatadogSite.Eu1;

            if (!string.IsNullOrWhiteSpace(siteName))
            {
                if (string.IsNullOrWhiteSpace(customEndpoint))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"MobileApm: Datadog site '{siteName}' is not one of " +
                        $"{string.Join(", ", Enum.GetNames<DatadogSite>())} and no CustomEndpoint is set — " +
                        "monitoring disabled rather than shipping telemetry to the wrong site.");
                    return;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"MobileApm: Datadog site '{siteName}' has no DatadogSite member — " +
                    $"routing logs and RUM to CustomEndpoint '{customEndpoint}'.");
            }
        }

        // A null config/endpoint on either feature leaves the SDK's own site-derived
        // intake in place — which is what every site the enum can name wants.
        var logsConfiguration = string.IsNullOrWhiteSpace(customEndpoint)
            ? null
            : new DdLogsConfiguration { CustomEndpoint = customEndpoint };

        builder
            .UseDatadog(new DdSdkConfiguration
            {
                ClientToken = clientToken,
                Environment = AppConfig.EnvironmentName,
                TrackingConsent = TrackingConsent.Granted,
                Service = "carditrack-mobile",
                Site = site,
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
            .UseDatadogLogs(logsConfiguration)
            .UseDatadogRum(new DdRumConfiguration
            {
                ApplicationId = applicationId,
                SessionSampleRate = 100.0,
                CustomEndpoint = customEndpoint,
            });
#endif
    }
}
