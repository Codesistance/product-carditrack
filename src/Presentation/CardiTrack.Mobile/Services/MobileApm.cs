using CardiTrack.Mobile.Core.Diagnostics;
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
    /// <summary>
    /// True once an engine has actually been wired up. Callers that reach for an SDK's
    /// static entry points later — <see cref="DiagnosticsConsent"/> does — need to know
    /// the difference between "monitoring is off" and "monitoring is on and silent".
    /// </summary>
    public static bool IsConfigured { get; private set; }

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
    /// Datadog logs and traces, plus RUM with native crash reporting when the data names a
    /// RUM application. Data: {"ClientToken":"pub...","ApplicationId":"...","Site":"Uk1",
    /// "IntakeHost":"browser-intake-uk1-datadoghq.com"} — client token and application id
    /// are write-only identifiers, safe to embed. Session Replay is deliberately NOT enabled:
    /// health data must not be recorded.
    /// </summary>
    /// <remarks>
    /// IntakeHost exists because this org's site, UK1, has no member in Datadog.Maui's
    /// DatadogSite enum or in the native SDKs it bundles, and a site the SDK cannot name is
    /// routed to a fallback region — a different org. With an IntakeHost every feature is
    /// pointed at that host through the SDK's per-feature custom endpoint, which the native
    /// wrappers apply; the URLs are composed by <see cref="DatadogIntake"/>, which explains
    /// why they must be full per-feature URLs and never the bare host.
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

        DatadogIntake? intake = null;
        var intakeHost = data.Value<string>("IntakeHost");
        if (!string.IsNullOrWhiteSpace(intakeHost) && !DatadogIntake.TryCreate(intakeHost, out intake))
        {
            Log.Warning(
                "MobileApm: Datadog IntakeHost '{IntakeHost}' is not a bare host name — monitoring disabled " +
                "rather than shipping telemetry to the wrong place.",
                intakeHost);
            return;
        }

        var siteName = data.Value<string>("Site");

        // Without an IntakeHost the site is the only thing routing telemetry, and one the enum
        // cannot name would silently ship it to whatever the fallback resolved to — a different
        // org in a different region — so refuse to start monitoring rather than misdeliver it.
        // With an IntakeHost every feature is routed explicitly and the site is informational,
        // so an unnameable one (Uk1) just leaves the core at the documented Eu1 default, which
        // nothing is sent to. IsDefined is what rejects a numeric Site: TryParse happily turns
        // "42" into an enum value no member names.
        if (!Enum.TryParse<DatadogSite>(siteName, ignoreCase: true, out var site) || !Enum.IsDefined(site))
        {
            if (!string.IsNullOrWhiteSpace(siteName) && intake is null)
            {
                Log.Warning(
                    "MobileApm: Datadog site '{Site}' is not one of {KnownSites} and no IntakeHost is set — " +
                    "monitoring disabled rather than shipping telemetry to the wrong site.",
                    siteName, string.Join(", ", Enum.GetNames<DatadogSite>()));
                return;
            }

            site = DatadogSite.Eu1;
        }

        var applicationId = data.Value<string>("ApplicationId");
        var rumEnabled = !string.IsNullOrWhiteSpace(applicationId);

        builder
            .UseDatadog(new DdSdkConfiguration
            {
                ClientToken = clientToken,
                Environment = AppConfig.EnvironmentName,
                // Opt-in: nothing is collected until the caregiver turns diagnostics on in
                // Settings, and DiagnosticsConsent.Set flips this at runtime from there.
                TrackingConsent = DiagnosticsConsent.IsGranted
                    ? TrackingConsent.Granted
                    : TrackingConsent.NotGranted,
                Service = "carditrack-mobile",
                Site = site,
                // Datadog's crash reporting rides on RUM: without RUM there is nothing to carry
                // the reports, and Play Console vitals is the source for crashes and ANRs.
                NativeCrashReportEnabled = rumEnabled,
#if DEBUG
                // Each batch upload's status in logcat / the Xcode console — the only way to see
                // from a device whether an intake accepted anything.
                Verbosity = SdkVerbosity.DEBUG,
#endif
                // Marks our API as first-party so mobile spans join the API's OTel traces, via
                // W3C traceparent headers.
                FirstPartyHosts =
                [
                    new FirstPartyHost
                    {
                        Match = new Uri(AppConfig.ApiBaseUrl).Host,
                        HeaderTypes = [TracingHeaderType.Datadog, TracingHeaderType.TraceContext],
                    },
                ],
            })
            .UseDatadogLogs(intake is null ? null : new DdLogsConfiguration { CustomEndpoint = intake.Logs })
            .UseDatadogTrace(intake is null ? null : new DdTraceConfiguration { CustomEndpoint = intake.Traces });

        if (rumEnabled)
        {
            builder.UseDatadogRum(new DdRumConfiguration
            {
                ApplicationId = applicationId!,
                SessionSampleRate = 100.0,
                CustomEndpoint = intake?.Rum,
                // Views are named from the Shell route (query string stripped, so a member id
                // never reaches a view name) or the page class — never Page.Title, which can be
                // a member's name.
                AutomaticViewTracking = true,
                // An action is named after the control it hit, and a tapped member card's text
                // is that member's name.
                AutomaticActionTracking = false,
                // Resource URLs ship verbatim — journal search text and caregiver invite tokens
                // included — and the resource mapper cannot rewrite a URL the native start event
                // already carries.
                AutomaticResourceTracking = false,
                TrackBackgroundEvents = false,
            });
        }

        IsConfigured = true;
        Log.Information(
            "MobileApm: Datadog configured — logs, traces{Rum:l} via {Route:l}.",
            rumEnabled ? ", RUM and crash reporting" : " (no ApplicationId, so no RUM)",
            intake is null ? $"site {site}" : $"intake host {intake.Host}");
#endif
    }
}
