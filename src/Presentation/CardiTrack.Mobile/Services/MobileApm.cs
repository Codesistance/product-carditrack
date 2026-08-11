using CardiTrack.Shared.Json;
using Newtonsoft.Json.Linq;
using Serilog;
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
/// Skip reasons go through Serilog (configured by AppLogging just before this runs) so
/// they reach the on-device log file in Release builds, where Debug.WriteLine is compiled
/// out and a silently disabled monitor would look identical to a working one.
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
            Log.Warning(
                "MobileApm: unknown engine '{Engine}' (known: {KnownEngines}) — monitoring disabled.",
                AppConfig.ApmEngine, string.Join(", ", Engines.Keys));
            return;
        }

        if (!JsonUtility.TryParse(AppConfig.ApmData, out var data, out var errors) || data is not JObject payload)
        {
            Log.Warning(
                "MobileApm: ApmData is not a JSON object — monitoring disabled. {Errors}",
                string.Join("; ", errors));
            return;
        }

        configure(builder, payload);
    }

    /// <summary>
    /// Datadog logs and traces. Data: {"ClientToken":"pub...","Site":"Eu1"} — the client
    /// token is a write-only identifier, safe to embed. Session Replay is deliberately NOT
    /// enabled: health data must not be recorded.
    /// </summary>
    /// <remarks>
    /// RUM is deliberately not enabled. It only ever returned 404 from the intake, because
    /// this org's site (UK1) has no member in Datadog.Maui's DatadogSite enum — nor in the
    /// dd-sdk-android core enum it wraps, which names only us1/us3/us5/eu1/ap1/ap2 and the
    /// gov sites. The documented workaround, CustomEndpoint, is inert: Datadog.Maui 0.2.0
    /// (the only version ever published) never calls the native useCustomEndpoint on any
    /// feature, so every feature targets the site-derived intake regardless of what is
    /// configured here. Removing RUM also removes Datadog crash reporting, which is a RUM
    /// feature — Play Console vitals remains the source for crashes and ANRs.
    /// </remarks>
    private static void ConfigureDatadog(MauiAppBuilder builder, JObject data)
    {
#if ANDROID || IOS
        var clientToken = data.Value<string>("ClientToken");
        if (string.IsNullOrWhiteSpace(clientToken))
        {
            Log.Warning("MobileApm: Datadog data needs a ClientToken — monitoring disabled.");
            return;
        }

        var siteName = data.Value<string>("Site");

        // A site the enum cannot name would silently ship this app's telemetry to whatever
        // the fallback resolved to — a different org in a different region — so refuse to
        // start monitoring at all rather than misdeliver it. CustomEndpoint used to be the
        // documented escape hatch here; it is not one (see the remarks above), so an
        // unnameable site is now simply fatal to monitoring. Omitting Site entirely keeps
        // the documented Eu1 default. IsDefined is what rejects a numeric Site: TryParse
        // happily turns "42" into an enum value no member names.
        if (!Enum.TryParse<DatadogSite>(siteName, ignoreCase: true, out var site) || !Enum.IsDefined(site))
        {
            if (!string.IsNullOrWhiteSpace(siteName))
            {
                Log.Warning(
                    "MobileApm: Datadog site '{Site}' is not one of {KnownSites} — monitoring disabled " +
                    "rather than shipping telemetry to the wrong site.",
                    siteName, string.Join(", ", Enum.GetNames<DatadogSite>()));
                return;
            }

            site = DatadogSite.Eu1;
        }

        builder
            .UseDatadog(new DdSdkConfiguration
            {
                ClientToken = clientToken,
                Environment = AppConfig.EnvironmentName,
                TrackingConsent = TrackingConsent.Granted,
                Service = "carditrack-mobile",
                Site = site,
                // Datadog's own crash reporting rides on RUM, which is not enabled — Play
                // Console vitals is the source for mobile crashes and ANRs instead.
                NativeCrashReportEnabled = false,
                // Marks our API as first-party so mobile spans join the API's OTel traces,
                // via W3C traceparent headers.
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
            .UseDatadogTrace();
#endif
    }
}
