using System.Diagnostics;
using System.Diagnostics.Metrics;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// Application's own <see cref="ActivitySource"/>/<see cref="Meter"/> instance for the push
/// delivery spine — same registered name as CardiTrack.Infrastructure.Diagnostics.PushTelemetry
/// (<see cref="TelemetryNames.PushSource"/>), a second instance rather than a project reference:
/// OpenTelemetry's <c>AddSource</c>/<c>AddMeter</c> subscribe by string name, not by instance
/// identity (see TelemetryNames' remarks), and Application must not reference Infrastructure.
/// </summary>
internal static class PushDispatchTelemetry
{
    public static readonly ActivitySource Source = new(TelemetryNames.PushSource);
    public static readonly Meter Meter = new(TelemetryNames.PushSource);

    /// <summary>
    /// The SLO metric (notification_engine.md §6.1) — p99 for Safety must stay under 60s. Lives
    /// here rather than on Infrastructure's PushTelemetry because the thing it measures — elapsed
    /// time to <see cref="AckDeliveryService.MarkDeliveredAsync"/> — happens in Application.
    /// </summary>
    public static readonly Histogram<double> TimeToAck = Meter.CreateHistogram<double>(
        "notification.time_to_ack", unit: "s", description: "Seconds from enqueue to client ack");

    public const string DeliveryIdTag = TelemetryNames.PushDeliveryIdTag;
    public const string CategoryTag = "notification.category";
    public const string ChannelTag = "notification.channel";
    public const string DedupHitTag = "notification.dedup_hit";
    public const string AttemptNumberTag = "notification.attempt_number";
    public const string StateTag = "notification.state";
}
