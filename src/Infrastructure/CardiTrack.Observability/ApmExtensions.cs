using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace CardiTrack.Observability;

/// <summary>
/// App-facing entry points. Both are silent no-ops until the Apm section is fully
/// configured, so dev machines ship nothing. Free-tier prudence is enforced here,
/// engine-independently: Warning+ logs only, head-sampled traces, health probes
/// never traced, and no metrics export — meters stream around the clock and would
/// drain a free plan fastest.
/// </summary>
public static class ApmExtensions
{
    public static ApmOptions GetApmOptions(this IConfiguration configuration) =>
        configuration.GetSection(ApmOptions.SectionName).Get<ApmOptions>() ?? new ApmOptions();

    /// <summary>
    /// Serilog side. Call while building the logger — this runs pre-DI (bootstrap
    /// logging), which is why the provider comes from the registry, not the container.
    /// </summary>
    public static LoggerConfiguration AddApmShipping(this LoggerConfiguration loggerConfiguration, ApmOptions options)
    {
        if (!options.IsConfigured)
            return loggerConfiguration;

        return ApmProviderRegistry.Resolve(options.Engine!).AddLogShipping(loggerConfiguration, options);
    }

    /// <summary>OTel side: traces exported to the configured APM backend.</summary>
    public static WebApplicationBuilder AddApmTracing(this WebApplicationBuilder builder, string serviceName)
    {
        var options = builder.Configuration.GetApmOptions();
        builder.Services.AddSingleton(options);

        if (!options.IsConfigured)
            return builder;

        var provider = ApmProviderRegistry.Resolve(options.Engine!);
        builder.Services.AddSingleton(provider);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: serviceName,
                serviceVersion: System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.ClampedSampleRatio)))
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        instrumentation.RecordException = true;
                        instrumentation.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation();
                provider.AddTraceExporter(tracing, options);
            });

        return builder;
    }
}
