using Serilog.Events;

namespace CardiTrack.Observability;

/// <summary>
/// Marks a log event as written on behalf of another service, so the shipping sink files it
/// under that service's name instead of the host's. Today the one relayed service is the
/// mobile app (<see cref="ApmServiceNames.Mobile"/>): its SDK cannot reach this org's Datadog
/// site, so the API accepts its Error+ lines over HTTP and re-emits them carrying this
/// property (see <c>MobileDiagnosticsController</c>).
/// </summary>
/// <remarks>
/// A property rather than a second logger because the API's request pipeline — the
/// <c>ClientVersion</c>/<c>ClientPlatform</c> scope, the activity enricher, the console sink —
/// should all still apply; only the resource the event ships under changes. The provider
/// splits its sink on this property: host events exclude it, the relay sink includes only it.
/// </remarks>
public static class LogRelay
{
    /// <summary>
    /// Pushed via <c>LogContext.PushProperty</c> around the relayed write. Public because it is
    /// the contract between the controller that sets it and the provider that routes on it.
    /// </summary>
    public const string ServiceProperty = "RelayedService";

    public static bool IsRelayed(LogEvent logEvent) =>
        logEvent.Properties.ContainsKey(ServiceProperty);

    public static bool IsRelayedTo(LogEvent logEvent, string serviceName) =>
        logEvent.Properties.TryGetValue(ServiceProperty, out var value)
        && value is ScalarValue { Value: string relayed }
        && string.Equals(relayed, serviceName, StringComparison.Ordinal);
}
