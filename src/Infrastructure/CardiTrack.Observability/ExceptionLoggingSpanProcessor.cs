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

    // Standard OTel span tags that either duplicate the exception type/message already in the
    // log message, or are plumbing (service.type/service.method from TracingProxy) rather than
    // business context — excluded from Context so it surfaces only what a caller wouldn't
    // otherwise get from the generic message, e.g. notification.phase, notification.delivery_id.
    private static readonly HashSet<string> ExcludedTagKeys =
    [
        "error.type", "error.message", "service.type", "service.method",
    ];

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
            var context = FormatContext(activity);

            _logger.LogError(
                "Span exception on {DisplayName}: {ExceptionType} - {ExceptionMessage}{Context}",
                activity.DisplayName, type, message, context);
        }
    }

    /// <summary>
    /// Renders the activity's own tags (e.g. <c>notification.phase</c>, <c>notification.delivery_id</c>)
    /// as trailing " (key=value, ...)" text, so a call site can drop its own catch-block LogError in
    /// favour of this one without losing the business context that made the specific log worth having.
    /// </summary>
    private static string FormatContext(Activity activity)
    {
        var pairs = activity.TagObjects
            .Where(t => !ExcludedTagKeys.Contains(t.Key) && t.Value is not null)
            .Select(t => $"{t.Key}={t.Value}")
            .ToList();

        return pairs.Count == 0 ? string.Empty : $" ({string.Join(", ", pairs)})";
    }
}
