using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace CardiTrack.Observability;

/// <summary>
/// Re-logs a failed span's exception as a normal structured log line. <c>RecordException = true</c>
/// on ASP.NET Core instrumentation already attaches the exception to the span as an
/// <see cref="ActivityEvent"/>, but that's only visible if you're looking at that specific span —
/// this makes it searchable/alertable in Datadog Logs like every other error. Read-only observer:
/// it never mutates or drops the span, the OTLP exporter still processes it normally. Ported from
/// ConcairgeApp's identically-named class.
/// </summary>
public sealed class ExceptionLoggingSpanProcessor : BaseProcessor<Activity>
{
    private const string ExceptionEventName = "exception";
    private const string ExceptionTypeTag = "exception.type";
    private const string ExceptionMessageTag = "exception.message";

    private readonly ILogger<ExceptionLoggingSpanProcessor> _logger;

    public ExceptionLoggingSpanProcessor(ILogger<ExceptionLoggingSpanProcessor> logger)
    {
        _logger = logger;
    }

    public override void OnEnd(Activity activity)
    {
        if (activity.Status != ActivityStatusCode.Error)
            return;

        foreach (var activityEvent in activity.Events)
        {
            if (activityEvent.Name != ExceptionEventName)
                continue;

            var type = activityEvent.Tags.FirstOrDefault(t => t.Key == ExceptionTypeTag).Value;
            var message = activityEvent.Tags.FirstOrDefault(t => t.Key == ExceptionMessageTag).Value;

            _logger.LogError(
                "Span exception on {DisplayName}: {ExceptionType} - {ExceptionMessage}",
                activity.DisplayName, type, message);
        }
    }
}
