using OpenTelemetry.Trace;

namespace CardiTrack.Observability;

/// <summary>
/// Drops orphan Npgsql spans at the root. Npgsql's own <c>NpgsqlActivitySource.CommandStart</c>
/// names every <c>CommandType.Text</c> command activity the constant <c>"postgresql"</c> — the
/// case for virtually every EF Core-issued query — with no tags set until after the activity
/// exists. CardiTrack.Worker and CardiTrack.PipelineJobs run as cron/scheduled-job hosts with no
/// inbound HTTP request, so every one of their queries becomes its own single-span trace named
/// "postgresql": no operation, no job context, nothing to correlate it to — pure noise.
///
/// This only ever runs for root (parentless) spans: it is meant to wrap the inner sampler passed
/// to <see cref="ParentBasedSampler"/>'s root slot, and ParentBasedSampler already routes any
/// span that has a parent through its own parent-based logic without calling this sampler at
/// all. So an Npgsql query nested under a real api/web request span is untouched — only a
/// genuinely orphan "postgresql" span is dropped.
/// </summary>
public sealed class NoiseFilteringSampler : Sampler
{
    private const string OrphanNpgsqlTextCommandName = "postgresql";

    private readonly Sampler _inner;

    public NoiseFilteringSampler(Sampler inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        samplingParameters.Name == OrphanNpgsqlTextCommandName
            ? new SamplingResult(SamplingDecision.Drop)
            : _inner.ShouldSample(samplingParameters);
}
