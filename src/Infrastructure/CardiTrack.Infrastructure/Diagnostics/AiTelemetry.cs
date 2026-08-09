using System.Diagnostics;
using System.Diagnostics.Metrics;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.Infrastructure.Diagnostics;

/// <summary>
/// Instruments for AI client calls, following the OpenTelemetry GenAI semantic conventions.
/// The ActivitySource and Meter share one name (<see cref="TelemetryNames.AiSource"/>) —
/// ApmExtensions subscribes to both by it; when no APM engine is configured there is no
/// listener and every emission here is a no-op.
///
/// Privacy invariant (DPIA): nothing recorded through this class may ever carry prompt text
/// or model output. Token counts, durations, model names and error types only.
/// </summary>
public static class AiTelemetry
{
    public static readonly ActivitySource Source = new(TelemetryNames.AiSource);
    public static readonly Meter Meter = new(TelemetryNames.AiSource);

    /// <summary>
    /// End-to-end client duration per AI operation, in seconds. Semconv-recommended buckets,
    /// extended past 81.92 s because MedGemma calls run against a 300 s client timeout and a
    /// scale-to-zero cold start routinely lands in the minutes range.
    /// </summary>
    public static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "gen_ai.client.operation.duration",
        unit: "s",
        description: "Duration of AI client operations",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries =
            [
                0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12,
                10.24, 20.48, 40.96, 81.92, 163.84, 327.68
            ]
        });

    /// <summary>
    /// Tokens consumed per call, split by <see cref="TokenTypeTag"/> (input = prompt
    /// evaluation, output = generation). Recorded only when the provider reports counts.
    /// </summary>
    public static readonly Histogram<long> TokenUsage = Meter.CreateHistogram<long>(
        "gen_ai.client.token.usage",
        unit: "{token}",
        description: "Tokens used per AI client operation",
        advice: new InstrumentAdvice<long>
        {
            HistogramBucketBoundaries = [1, 4, 16, 64, 256, 1024, 4096, 16384, 65536]
        });

    // GenAI semantic-convention attribute names, shared between spans, metrics and tests.
    public const string OperationNameTag = "gen_ai.operation.name";
    public const string ProviderNameTag = "gen_ai.provider.name";
    /// <summary>Predecessor of gen_ai.provider.name — backends still key on it; drop once they don't.</summary>
    public const string SystemTag = "gen_ai.system";
    public const string RequestModelTag = "gen_ai.request.model";
    public const string ResponseModelTag = "gen_ai.response.model";
    public const string InputTokensTag = "gen_ai.usage.input_tokens";
    public const string OutputTokensTag = "gen_ai.usage.output_tokens";
    public const string TokenTypeTag = "gen_ai.token.type";
    public const string ErrorTypeTag = "error.type";
}
