using System.Diagnostics;
using OpenTelemetry.Trace;

namespace CardiTrack.Observability;

/// <summary>
/// Drops orphan client-kind spans at the root — DB commands and outbound HTTP calls with no
/// parent context. The motivating case: Npgsql's own <c>NpgsqlActivitySource.CommandStart</c>
/// names every <c>CommandType.Text</c> command activity the constant <c>"postgresql"</c> — the
/// case for virtually every EF Core-issued query — with no tags set until after the activity
/// exists. CardiTrack.Worker and CardiTrack.PipelineJobs run as cron/scheduled-job hosts with no
/// inbound HTTP request, so every one of their queries becomes its own single-span trace named
/// "postgresql": no operation, no job context, nothing to correlate it to — pure noise. The same
/// is true of any other orphan <see cref="ActivityKind.Client"/> span (e.g. a startup HTTP probe
/// with no ambient request) — dropping by kind rather than by the one literal name subsumes the
/// Npgsql case and catches those too.
///
/// This only ever runs for root (parentless) spans: it is meant to wrap the inner sampler passed
/// to <see cref="ParentBasedSampler"/>'s root slot, and ParentBasedSampler already routes any
/// span that has a parent through its own parent-based logic without calling this sampler at
/// all. The <see cref="SamplingParameters.ParentContext"/> check is defense in depth for the
/// same guarantee, in case this sampler is ever wired outside a ParentBasedSampler by mistake.
/// So a DB/HTTP call nested under a real api/web request span is untouched — only a genuinely
/// orphan client-kind span is dropped.
/// </summary>
public sealed class NoiseFilteringSampler : Sampler
{
    private readonly Sampler _inner;

    public NoiseFilteringSampler(Sampler inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        samplingParameters.ParentContext.TraceId == default && samplingParameters.Kind == ActivityKind.Client
            ? new SamplingResult(SamplingDecision.Drop)
            : _inner.ShouldSample(samplingParameters);
}
