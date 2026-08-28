using System.Diagnostics;
using CardiTrack.Observability;
using CardiTrack.Shared.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace CardiTrack.UnitTests.Observability;

/// <summary>
/// Pins telemetry for the one host shape that does not start: <c>CardiTrack.PipelineJobs</c> runs
/// a single pass and exits without <c>app.Run()</c>.
/// </summary>
/// <remarks>
/// The failure being guarded against was total and silent. <c>AddOpenTelemetry</c> builds its
/// providers from an <c>IHostedService</c>, so a host that never starts never builds one — and a
/// process with no <see cref="TracerProvider"/> has no <see cref="ActivityListener"/>, which makes
/// every <c>StartActivity</c> return null rather than merely unsampled. The job then records no
/// spans at all, and no log line carries a trace_id either, because the enricher reads
/// <see cref="Activity.Current"/>. Datadog showed exactly that: zero <c>pipeline-jobs</c> spans
/// over seven days while every other service traced normally.
/// </remarks>
public class JobHostTelemetryTests
{
    /// <summary>
    /// The shipped state, kept as the other half of the pair below: without the providers built,
    /// a registered source does not merely produce an unsampled span — it produces no
    /// <see cref="Activity"/> at all, and nothing sets <see cref="Activity.Current"/> for the
    /// enricher to read. Sampling is 1.0 here, so a null result cannot be blamed on a drop.
    /// </summary>
    [Fact]
    public async Task WithoutStartTelemetry_AJobHostCreatesNoActivityAtAll()
    {
        await using var app = JobHostApp();

        using var source = new ActivitySource(TelemetryNames.PipelineSource);
        using var activity = source.StartActivity("digest");

        Assert.Null(activity);
        Assert.Null(Activity.Current);
    }

    [Fact]
    public async Task StartTelemetry_LetsAJobHostRecordSpans_ThoughItNeverRuns()
    {
        await using var app = JobHostApp();

        app.Services.StartTelemetry();

        using var source = new ActivitySource(TelemetryNames.PipelineSource);
        using var activity = source.StartActivity("digest");

        Assert.NotNull(activity);
        Assert.NotEqual(default, activity!.TraceId);
    }

    /// <summary>
    /// The trace/log join is the point of the above, so it is asserted rather than inferred: the
    /// enricher writes the ambient activity's ids onto a log event, and an ambient activity is
    /// precisely what a job host lacks until the providers are built.
    /// </summary>
    [Fact]
    public async Task StartTelemetry_GivesLogLinesAnAmbientActivity_ToCorrelateOn()
    {
        await using var app = JobHostApp();
        app.Services.StartTelemetry();

        using var source = new ActivitySource(TelemetryNames.PipelineSource);
        using var activity = source.StartActivity("digest");

        Assert.NotNull(Activity.Current);
        Assert.Equal(activity!.TraceId, Activity.Current!.TraceId);
    }

    [Fact]
    public async Task StartTelemetry_IsSafe_OnAHostThatShipsNothing()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.AddApmTracing(ApmServiceNames.PipelineJobs);
        await using var app = builder.Build();

        app.Services.StartTelemetry();

        Assert.Null(app.Services.GetService<TracerProvider>());
    }

    /// <summary>
    /// Built the way <c>CardiTrack.PipelineJobs</c> builds it — fully configured APM, and no
    /// <c>Run()</c>. Sampling is pinned to 1.0 because the assertions are about whether an
    /// activity exists at all: at the 0.2 default, a dropped span is also a null one, and the
    /// test would fail four times in five for the wrong reason.
    /// </summary>
    private static WebApplication JobHostApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Apm:Engine"] = "Datadog",
            ["Apm:Data:IngestUrl"] = "uk1.datadoghq.com",
            ["Apm:Data:IngestToken"] = "token-123",
            ["Apm:Data:Extra:TraceEndpoint"] = "https://otlp.uk1.datadoghq.com/v1/traces",
            ["Apm:TracesSampleRatio"] = "1.0",
        });
        builder.AddApmTracing(ApmServiceNames.PipelineJobs);
        return builder.Build();
    }
}
