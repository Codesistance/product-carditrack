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
    /// <summary>
    /// Loads the Apm section. Data comes in one of two forms: a nested section
    /// (appsettings), or — the deployment contract — a single JSON value from the
    /// Apm__Data env var backed by one secret. The single-value form wins when both
    /// are present (env vars overlay appsettings without removing its children).
    /// </summary>
    public static ApmOptions GetApmOptions(this IConfiguration configuration)
    {
        var section = configuration.GetSection(ApmOptions.SectionName);

        // Bound by hand: the default binder throws on a string value where a complex
        // type is expected, which is exactly what Apm__Data-as-JSON looks like to it.
        var options = new ApmOptions
        {
            Engine = section[nameof(ApmOptions.Engine)],
        };
        if (section[nameof(ApmOptions.MinimumLogLevel)] is { } shipLevel)
            options.MinimumLogLevel = shipLevel;
        if (double.TryParse(section[nameof(ApmOptions.TracesSampleRatio)],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ratio))
            options.TracesSampleRatio = ratio;

        var dataSection = section.GetSection(nameof(ApmOptions.Data));
        if (ApmOptions.HasRealValue(dataSection.Value))
            options.Data = ApmData.FromJson(dataSection.Value!.Trim());
        else if (dataSection.Value is null)
            options.Data = dataSection.Get<ApmData>() ?? new ApmData();
        // else: empty/placeholder single value — Data stays unset, shipping stays off

        return options;
    }

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
