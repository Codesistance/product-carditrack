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

    private static readonly ActivityContext ParentedContext = new(
        ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);

    private static SamplingParameters Parameters(string name, ActivityKind kind, ActivityContext parentContext) =>
        new(parentContext, ActivityTraceId.CreateRandom(), name, kind);

    private static SamplingParameters OrphanClientParameters(string name) =>
        Parameters(name, ActivityKind.Client, default);

    [Theory]
    [InlineData("postgresql")]
    [InlineData("GET /api/members")]
    public void ShouldSample_Drops_OrphanClientSpan_RegardlessOfName(string name)
    {
        var sampler = new NoiseFilteringSampler(new StubSampler());

        var result = sampler.ShouldSample(OrphanClientParameters(name));

        Assert.Equal(SamplingDecision.Drop, result.Decision);
    }

    [Fact]
    public void ShouldSample_DelegatesToInner_WhenTheSpanHasAParent()
    {
        var sampler = new NoiseFilteringSampler(new StubSampler());

        var result = sampler.ShouldSample(Parameters("postgresql", ActivityKind.Client, ParentedContext));

        Assert.Equal(SamplingDecision.RecordAndSample, result.Decision);
    }

    [Fact]
    public void ShouldSample_DelegatesToInner_WhenTheKindIsNotClient()
    {
        var sampler = new NoiseFilteringSampler(new StubSampler());

        var result = sampler.ShouldSample(Parameters("GET /api/members", ActivityKind.Server, default));

        Assert.Equal(SamplingDecision.RecordAndSample, result.Decision);
    }
}
