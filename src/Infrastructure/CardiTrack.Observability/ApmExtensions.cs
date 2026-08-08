using CardiTrack.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace CardiTrack.Observability;

/// <summary>
/// App-facing entry points. Both are no-ops until the Apm section is fully configured,
/// so dev machines ship nothing. Free-tier prudence is enforced here, engine-
/// independently: logs at the Serilog root level and no lower (Warning unless a service
/// is turned up on purpose), head-sampled traces, health probes never traced,
/// and metrics only behind the explicit Apm:MetricsEnabled switch (apm_metrics_enabled
/// tfvar) — meters stream around the clock and would drain a free plan fastest.
/// </summary>
public static class ApmExtensions
{
    /// <summary>
    /// Engine selection: reads the Apm:Engine key through the universal reader
    /// (<see cref="ConfigurationLoader"/> — env var Apm__Engine wins over appsettings)
    /// and resolves the provider it names, so when the value is "BetterStack" the
    /// BetterStack provider is what gets injected. Returns null when the key is unset
    /// or a placeholder; an unknown name fails loudly in the registry.
    /// </summary>
    public static IApmProvider? LoadEngine(this IConfiguration configuration)
    {
        var engine = new ConfigurationLoader(configuration).Get(ConfigurationKeys.Apm.Engine);
        return ApmOptions.HasRealValue(engine) ? ApmProviderRegistry.Resolve(engine!) : null;
    }

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
            Engine = new ConfigurationLoader(configuration).Get(ConfigurationKeys.Apm.Engine),
        };
        if (ApmOptions.HasRealValue(section[nameof(ApmOptions.MinimumLogLevel)]))
            options.MinimumLogLevel = section[nameof(ApmOptions.MinimumLogLevel)];

        // Unpinned, the sink follows the Serilog root, so raising one service to Information
        // ships that service's Information logs too instead of only widening the console.
        // Both Serilog spellings are accepted: the object form Terraform writes per service
        // (Serilog__MinimumLevel__Default) and the flat string form.
        options.InheritedLogLevel = configuration["Serilog:MinimumLevel:Default"]
            ?? configuration["Serilog:MinimumLevel"];
        if (double.TryParse(section[nameof(ApmOptions.TracesSampleRatio)],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ratio))
            options.TracesSampleRatio = ratio;
        if (bool.TryParse(section[nameof(ApmOptions.MetricsEnabled)], out var metricsEnabled))
            options.MetricsEnabled = metricsEnabled;

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
    /// Pass the host's <see cref="ApmServiceNames"/> constant, the same one it later
    /// hands <see cref="AddApmTracing"/>: logs and traces have to name the service
    /// identically for a backend to tie them together.
    /// </summary>
    public static LoggerConfiguration AddApmShipping(
        this LoggerConfiguration loggerConfiguration, ApmOptions options, string serviceName)
    {
        if (!options.IsConfigured)
            return loggerConfiguration;

        return ApmProviderRegistry.Resolve(options.Engine!).AddLogShipping(loggerConfiguration, options, serviceName);
    }

    /// <summary>
    /// OTel side: traces (and, behind the Apm:MetricsEnabled switch, metrics) exported
    /// to the configured APM backend. Also the startup-log seam for the whole Apm load:
    /// every host calls this right after its Serilog logger exists, so this is where a
    /// successful load is announced and a broken one says exactly why it ships nothing.
    /// </summary>
    public static WebApplicationBuilder AddApmTracing(this WebApplicationBuilder builder, string serviceName)
    {
        var options = builder.Configuration.GetApmOptions();
        builder.Services.AddSingleton(options);

        // Load the engine and inject the provider it names whenever one is selected,
        // so the DI shape is stable; the exporters below still require full config.
        var provider = builder.Configuration.LoadEngine();
        if (provider is not null)
            builder.Services.AddSingleton(provider);

        if (provider is null || !options.IsConfigured)
        {
            LogDisabled(options, provider);
            return builder;
        }

        var status = provider.Describe(options);
        Log.Information(
            "APM configured: engine {Engine} shipping {Signals} to {IngestUrl} as {ServiceName} {ServiceVersion} "
            + "(log ship level {ShipLevel}, trace sampling {SampleRatio:P0}, metrics switch {MetricsSwitch})",
            provider.Name, status.Summary, options.Data.IngestUrl, serviceName, DeploymentInfo.Version,
            options.ShipLevel, options.ClampedSampleRatio, options.MetricsEnabled ? "on" : "off");
        foreach (var warning in status.Warnings)
            Log.Warning("APM ({Engine}): {Reason}", provider.Name, warning);

        var telemetry = builder.Services.AddOpenTelemetry()
            // service.version is the release the spans belong to — the deploy's semver tag,
            // not the assembly version (which stays at the SDK default nobody stamps).
            // Backends key their release comparisons on it.
            .ConfigureResource(resource => resource.AddService(
                serviceName: serviceName,
                serviceVersion: DeploymentInfo.Version))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.ClampedSampleRatio)))
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        instrumentation.RecordException = true;
                        // /health (API, Web) and /healthz (Worker) — probe traffic is never traced
                        instrumentation.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health")
                            && !context.Request.Path.StartsWithSegments("/healthz");
                    })
                    .AddHttpClientInstrumentation()
                    // Npgsql's built-in ActivitySource: one span per database command,
                    // parented under the request trace (what Npgsql.OpenTelemetry's
                    // AddNpgsql() registers, without pinning another package version).
                    .AddSource("Npgsql");
                provider.AddTraceExporter(tracing, options);
            });

        if (options.MetricsEnabled)
            telemetry.WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    // Built-in .NET runtime meter (GC, JIT, thread pool, exceptions)
                    // and Npgsql's meter (connections, commands) — no extra packages.
                    .AddMeter("System.Runtime")
                    .AddMeter("Npgsql");
                provider.AddMetricExporter(metrics, options);
            });

        return builder;
    }

    /// <summary>
    /// Says why nothing will ship. An entirely empty Apm section is the intended local
    /// setup and logs as Information; anything half-set is a misconfiguration worth a
    /// Warning naming the missing piece.
    /// </summary>
    private static void LogDisabled(ApmOptions options, IApmProvider? provider)
    {
        var hasAnyData = ApmOptions.HasRealValue(options.Data.IngestUrl)
            || ApmOptions.HasRealValue(options.Data.IngestToken);

        if (provider is null)
        {
            if (hasAnyData)
                Log.Warning(
                    "APM shipping disabled: Apm:Data is set but Apm:Engine is empty or a placeholder — "
                    + "set Apm:Engine to one of: {KnownEngines}", string.Join(", ", ApmProviderRegistry.KnownEngines));
            else
                Log.Information("APM shipping disabled: no engine configured — logs stay on console only");
            return;
        }

        var missing = new List<string>();
        if (!ApmOptions.HasRealValue(options.Data.IngestUrl))
            missing.Add($"{nameof(ApmData.IngestUrl)}");
        if (!ApmOptions.HasRealValue(options.Data.IngestToken))
            missing.Add($"{nameof(ApmData.IngestToken)}");
        Log.Warning(
            "APM shipping disabled: engine {Engine} is selected but Apm:Data is incomplete — "
            + "{Missing} missing or still the Terraform placeholder (fix the apm-data secret and re-roll)",
            provider.Name, string.Join(" and ", missing));
    }
}
