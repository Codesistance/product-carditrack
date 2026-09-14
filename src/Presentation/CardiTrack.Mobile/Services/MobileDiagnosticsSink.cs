using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Mobile.Core.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// The Serilog end of the relay: turns each Error-and-above event into a
/// <see cref="MobileDiagnosticsLogEntry"/> and hands it to <see cref="IMobileDiagnosticsRelay"/>
/// to queue. Nothing is sent from here — sending is a network call and this runs inline with
/// the log write, on whatever thread wrote it. <c>AppLogging</c> decides when to flush: at
/// startup, on resume, and (blocking) in the unhandled-exception handler.
/// </summary>
public sealed class MobileDiagnosticsSink : ILogEventSink
{
    private readonly IMobileDiagnosticsRelay _relay;

    public MobileDiagnosticsSink(IMobileDiagnosticsRelay relay) => _relay = relay;

    public void Emit(LogEvent logEvent)
    {
        if (!_relay.Enabled)
            return;

        // The relay truncates to the contract's ceilings and normalises the level; this only
        // has to render faithfully. RenderMessage substitutes the properties in, so the entry
        // reads as the file sink's line does, not as the template. Everything beyond the line
        // itself — frames, screen, thread, device state, the log tail on a crash — is
        // MobileDiagnosticsContext's, each field guarded on its own.
        var entry = new MobileDiagnosticsLogEntry
        {
            Timestamp = logEvent.Timestamp,
            Level = logEvent.Level.ToString(),
            Message = logEvent.RenderMessage(),
            Exception = logEvent.Exception?.ToString(),
            Source = logEvent.Properties.TryGetValue("SourceContext", out var source)
                && source is ScalarValue { Value: string context }
                ? context
                : null,
        };
        MobileDiagnosticsContext.Enrich(entry, logEvent.Exception, isCrash: logEvent.Level == LogEventLevel.Fatal);
        _relay.Record(entry);
    }
}
