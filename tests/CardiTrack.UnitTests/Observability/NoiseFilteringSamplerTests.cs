using System.Diagnostics;
using CardiTrack.Observability;
using OpenTelemetry.Trace;

namespace CardiTrack.UnitTests.Observability;

public class NoiseFilteringSamplerTests
{
    private sealed class StubSampler : Sampler
    {
        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
            new(SamplingDecision.RecordAndSample);
    }

    private static SamplingParameters Parameters(string name) =>
        new(default, ActivityTraceId.CreateRandom(), name, ActivityKind.Client);

    [Fact]
    public void ShouldSample_Drops_OrphanPostgresqlSpan()
    {
        var sampler = new NoiseFilteringSampler(new StubSampler());

        var result = sampler.ShouldSample(Parameters("postgresql"));

        Assert.Equal(SamplingDecision.Drop, result.Decision);
    }

    [Fact]
    public void ShouldSample_DelegatesToInner_ForAnyOtherName()
    {
        var sampler = new NoiseFilteringSampler(new StubSampler());

        var result = sampler.ShouldSample(Parameters("GET /api/members"));

        Assert.Equal(SamplingDecision.RecordAndSample, result.Decision);
    }
}
