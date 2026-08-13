using Serilog.Core;
using Serilog.Events;

namespace CardiTrack.Observability.Providers;

/// <summary>
/// Stamps a lowercase canonical <c>level</c> attribute onto every log shipped to Datadog, so the
/// reserved log <b>status</b> comes out right.
///
/// Datadog's OTLP log intake resolves status by checking the record's attributes for
/// <c>status</c>/<c>severity</c>/<c>level</c>/<c>syslog.severity</c> first and falling back to
/// OTLP <c>severity_text</c> only when none is present — and it takes whichever it finds verbatim
/// (see DataDog/opentelemetry-mapping-go, pkg/otlp/logs/transform.go, whose comment says the list
/// mirrors the backend). With no such attribute, Serilog's severity_text lands in status as-is:
/// "Error", which is not the canonical "error", so <c>status:error</c> silently matches nothing.
/// This enricher wins that precedence race with a value that needs no further normalisation.
///
/// The mapping reproduces what Datadog's own C# integration pipeline produced back when logs
/// shipped on the classic intake (source:csharp) — measured from this org's own records, Fatal
/// landing on "emergency" included. Emitted pre-lowercased so nothing downstream has to know
/// Datadog never case-folds.
///
/// Registered inside <see cref="DatadogApmProvider.AddLogShipping"/>'s sub-logger only: the
/// attribute is a Datadog intake contract, not log content, so the console and any other engine
/// (BetterStack) never see it.
/// </summary>
public sealed class DatadogLogStatusEnricher : ILogEventEnricher
{
    /// <summary>
    /// Public because it is the contract: the one attribute name Datadog's intake checks that
    /// this enricher chose to occupy. Anything referencing it should do so by name.
    /// </summary>
    public const string AttributeName = "level";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var status = logEvent.Level switch
        {
            LogEventLevel.Verbose => "debug",
            LogEventLevel.Debug => "debug",
            LogEventLevel.Information => "info",
            LogEventLevel.Warning => "warn",
            LogEventLevel.Error => "error",
            LogEventLevel.Fatal => "emergency",
            // Serilog has exactly six levels; a seventh would mean the enum grew and this
            // mapping is stale. "info" over throwing: status must never break shipping.
            _ => "info",
        };

        // AddPropertyIfAbsent: an event that already carries a "level" property (e.g. from a
        // structured message template) keeps its own — clobbering user data to win an intake
        // convention would be backwards.
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(AttributeName, status));
    }
}
