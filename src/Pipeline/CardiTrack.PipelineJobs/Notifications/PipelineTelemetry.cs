using System.Diagnostics;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.PipelineJobs.Notifications;

/// <summary>
/// ActivitySource for the realtime notification pipeline. ApmExtensions subscribes to it by
/// <see cref="TelemetryNames.PipelineSource"/>; when no APM engine is configured there is no
/// listener and every emission here is a no-op.
/// </summary>
public static class PipelineTelemetry
{
    public static readonly ActivitySource Source = new(TelemetryNames.PipelineSource);

    /// <summary>
    /// Parses a W3C traceparent (as stamped by PubSubNotificationPublisher) into a link list for
    /// <see cref="ActivitySource.StartActivity(string, ActivityKind, ActivityContext, IEnumerable{KeyValuePair{string, object?}}?, IEnumerable{ActivityLink}?, DateTimeOffset)"/>.
    /// Delegates to the shared parser (<see cref="TraceLinking"/>) also used by the push spine's
    /// send→ack linking (AckDeliveryService) — one parse implementation for both call sites.
    /// </summary>
    public static IEnumerable<ActivityLink> BuildLinks(string? traceParent) => TraceLinking.BuildLinks(traceParent);
}
