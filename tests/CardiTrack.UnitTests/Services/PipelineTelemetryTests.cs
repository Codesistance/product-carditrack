using System.Diagnostics;
using CardiTrack.PipelineJobs.Notifications;

namespace CardiTrack.UnitTests.Services;

public class PipelineTelemetryTests
{
    [Fact]
    public void BuildLinks_ValidTraceParent_ReturnsOneLinkToItsTraceId()
    {
        using var activity = new Activity("publish").Start();
        var traceParent = activity.Id!;

        var links = PipelineTelemetry.BuildLinks(traceParent).ToList();

        var link = Assert.Single(links);
        Assert.Equal(activity.TraceId, link.Context.TraceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-traceparent")]
    public void BuildLinks_NullOrMalformed_ReturnsNoLinks(string? traceParent)
    {
        var links = PipelineTelemetry.BuildLinks(traceParent).ToList();

        Assert.Empty(links);
    }
}
