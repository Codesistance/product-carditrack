namespace CardiTrack.Shared.Telemetry;

/// <summary>
/// Names of the solution's own telemetry sources. OpenTelemetry subscribes to
/// ActivitySources and Meters by name, so the project that defines an instrument
/// (CardiTrack.Infrastructure) and the project that registers it for export
/// (CardiTrack.Observability) must agree on the string without referencing each
/// other — this class is the one place both can depend on.
/// </summary>
public static class TelemetryNames
{
    /// <summary>
    /// ActivitySource and Meter name for AI client calls (MedGemma). One name covers
    /// both signals: ApmExtensions passes it to AddSource and AddMeter.
    /// </summary>
    public const string AiSource = "CardiTrack.Ai";

    /// <summary>
    /// ActivitySource name for pipeline work: the job's own root span, the realtime notification
    /// pipeline (webhook-receiver's publish through pipeline-jobs' drain), and the per-member
    /// spans inside a pass. One name so ApmExtensions can register it for export, and two
    /// instances of it — PipelineJobs' <c>PipelineTelemetry</c> and Infrastructure's
    /// <c>JudgementTelemetry</c> — for the same reason <see cref="PushSource"/> has two.
    /// </summary>
    public const string PipelineSource = "CardiTrack.Pipeline";

    /// <summary>
    /// ActivitySource and Meter name for the push delivery spine (FCM sends, dispatch worker
    /// batches). FirebaseAdmin manages its own transport outside IHttpClientFactory, so this is
    /// the only signal an FCM call produces — not optional the way HttpClient auto-instrumentation
    /// would make it for a normal external call.
    /// </summary>
    public const string PushSource = "CardiTrack.Push";

    /// <summary>
    /// Span/tag key for a <c>NotificationDelivery</c> id, stamped on every push-spine span
    /// (enqueue, dispatch attempt, FCM send, worker tick, ack) so the whole lifecycle is
    /// filterable by one value even where the spans don't share a trace id.
    /// </summary>
    public const string PushDeliveryIdTag = "notification.delivery_id";

    /// <summary>
    /// Meter name for the family-questionnaire funnel (asked, answered, skipped, expired,
    /// volunteered, and whether the next digest kept or recited those facts). Counters only —
    /// there is no matching ActivitySource; the product question is a rate, not a trace.
    /// </summary>
    public const string QuestionnaireSource = "CardiTrack.Questionnaires";
}
