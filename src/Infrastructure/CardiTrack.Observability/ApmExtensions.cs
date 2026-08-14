using System.Net;
using System.Security.Claims;
using CardiTrack.Shared;
using CardiTrack.Shared.Telemetry;
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
            + "in {Environment} (log ship level {ShipLevel}, trace sampling {SampleRatio:P0}, "
            + "metrics switch {MetricsSwitch})",
            provider.Name, status.Summary, options.Data.IngestUrl, serviceName, DeploymentInfo.Version,
            DeploymentInfo.EnvironmentName ?? "no environment",
            options.ShipLevel, options.ClampedSampleRatio, options.MetricsEnabled ? "on" : "off");
        if (DeploymentInfo.EnvironmentName is null)
            Log.Warning(
                "APM ({Engine}): telemetry will ship without an environment — neither {OverrideKey} nor "
                + "{EnvironmentKey} is set, so this host's logs and traces cannot be told apart from any "
                + "other environment's", provider.Name, ConfigurationKeys.Deployment.Environment,
                ConfigurationKeys.Deployment.AspNetCoreEnvironment);
        foreach (var warning in status.Warnings)
            Log.Warning("APM ({Engine}): {Reason}", provider.Name, warning);

        // The backend's own ingest calls (log/trace/metrics shipping) must not become spans
        // themselves — otherwise every shipment traces itself, filling APM with self-referential
        // noise. They still ship fine; they're just invisible to HttpClientInstrumentation.
        var shippingHosts = provider.ShippingHosts(options);

        // A failed OTLP export (network error, non-2xx, serialization failure) is otherwise
        // completely silent — the SDK only reports it through its own EventSource, which nothing
        // subscribes to by default. Provider-agnostic so any engine gets this, not just Datadog.
        builder.Services.AddHostedService<OtlpExportDiagnostics>();

        // Re-logs a failed span's exception as a normal structured log line, so it's searchable
        // in Datadog Logs like any other error instead of only visible inside span data.
        builder.Services.AddSingleton<ExceptionLoggingSpanProcessor>();

        var telemetry = builder.Services.AddOpenTelemetry()
            // service.version is the release the spans belong to — the deploy's semver tag,
            // not the assembly version (which stays at the SDK default nobody stamps).
            // Backends key their release comparisons on it.
            .ConfigureResource(resource => ConfigureApmResource(resource, serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddProcessor(sp => sp.GetRequiredService<ExceptionLoggingSpanProcessor>())
                    .SetSampler(new ParentBasedSampler(
                        new NoiseFilteringSampler(new TraceIdRatioBasedSampler(options.ClampedSampleRatio))))
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        instrumentation.RecordException = true;
                        // /health (API, Web) and /healthz (Worker) — probe traffic is never traced
                        instrumentation.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health")
                            && !context.Request.Path.StartsWithSegments("/healthz");
                        // Client IP, masked (last IPv4 octet / last 16 IPv6 bits dropped) — useful
                        // for abuse/rate-limit investigation without pinning down an exact device.
                        instrumentation.EnrichWithHttpRequest = (activity, request) =>
                        {
                            var maskedIp = MaskClientIp(request.HttpContext.Connection.RemoteIpAddress);
                            if (maskedIp is not null)
                                activity.SetTag("http.client_ip", maskedIp);
                        };
                        // Fires at request end (Activity stop), not start — EnrichWithHttpRequest
                        // runs before the middleware pipeline (including authentication) has had a
                        // chance to populate HttpContext.User. Auth0UserId only, pseudonymous —
                        // never email: UserContextMiddleware.cs already made this call explicitly
                        // (pushing email into telemetry "made a health service's user list readable
                        // from telemetry"), and this must not reopen that.
                        instrumentation.EnrichWithHttpResponse = (activity, response) =>
                        {
                            var auth0UserId = response.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                            if (!string.IsNullOrEmpty(auth0UserId))
                                activity.SetTag("enduser.id", auth0UserId);
                        };
                    })
                    .AddHttpClientInstrumentation(instrumentation =>
                    {
                        instrumentation.FilterHttpRequestMessage = request =>
                            request.RequestUri is not { } uri || !shippingHosts.Contains(uri.Host);
                    })
                    // Npgsql's built-in ActivitySource: one span per database command,
                    // parented under the request trace (what Npgsql.OpenTelemetry's
                    // AddNpgsql() registers, without pinning another package version).
                    .AddSource("Npgsql")
                    // AI client calls (MedGemma): one GenAI-semconv span per call, defined
                    // in CardiTrack.Infrastructure's AiTelemetry.
                    .AddSource(TelemetryNames.AiSource)
                    // Realtime notification pipeline: one span per pulled Pub/Sub message,
                    // linked back to the publishing webhook-receiver span. Defined in
                    // CardiTrack.PipelineJobs' PipelineTelemetry.
                    .AddSource(TelemetryNames.PipelineSource)
                    // Push delivery spine: one span per FCM send. Load-bearing, not optional —
                    // FirebaseAdmin manages its own transport outside IHttpClientFactory, so
                    // AddHttpClientInstrumentation above never sees these calls. Defined in
                    // CardiTrack.Infrastructure's PushTelemetry.
                    .AddSource(TelemetryNames.PushSource);
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
                    .AddMeter("Npgsql")
                    // GenAI client metrics (gen_ai.client.operation.duration,
                    // gen_ai.client.token.usage) from AiTelemetry.
                    .AddMeter(TelemetryNames.AiSource)
                    // Push delivery spine counters/histograms (notification.* — enqueued, sent,
                    // delivered, failed, escalated, undelivered_critical, time_to_ack) from
                    // PushTelemetry. time_to_ack is the SLO metric (§6.1).
                    .AddMeter(TelemetryNames.PushSource);
                provider.AddMetricExporter(metrics, options);
            });

        return builder;
    }

    /// <summary>
    /// The resource every signal is stamped with: who is reporting (service.name), which
    /// build (service.version), and where it runs.
    ///
    /// The environment goes on under both spellings on purpose. "deployment.environment"
    /// is the original semantic-convention key and "deployment.environment.name" the one
    /// that replaced it; OTLP intakes are mid-migration and read one or the other, so
    /// sending both costs a duplicated string and removes the failure mode where every
    /// span arrives with no environment at all. Drop the older key once the backend in
    /// use is confirmed to read the newer one.
    /// </summary>
    private static ResourceBuilder ConfigureApmResource(ResourceBuilder resource, string serviceName)
    {
        resource.AddService(
            serviceName: serviceName,
            serviceVersion: DeploymentInfo.Version,
            serviceInstanceId: Environment.MachineName);

        resource.AddAttributes(
        [
            new KeyValuePair<string, object>("service.namespace", "carditrack"),
            new KeyValuePair<string, object>("host.name", Environment.MachineName),
            new KeyValuePair<string, object>("process.runtime.name", ".NET"),
            new KeyValuePair<string, object>("process.runtime.version", Environment.Version.ToString()),
        ]);

        if (DeploymentInfo.EnvironmentName is { } environmentName)
            resource.AddAttributes(
            [
                new KeyValuePair<string, object>("deployment.environment.name", environmentName),
                new KeyValuePair<string, object>("deployment.environment", environmentName),
            ]);

        return resource;
    }

    /// <summary>
    /// Zeroes the last IPv4 octet or the last 16 bits of an IPv6 address — enough to group by
    /// rough origin (abuse/rate-limit investigation) without pinning down an exact device.
    /// </summary>
    private static string? MaskClientIp(IPAddress? address)
    {
        if (address is null)
            return null;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            bytes[^1] = 0;
        else
            Array.Clear(bytes, bytes.Length - 2, 2);

        return new IPAddress(bytes).ToString();
    }

    /// <summary>
    /// Every host must call this on exit. <c>Log.Logger</c> is a static field set before
    /// the DI container exists, so nothing disposes it automatically the way host
    /// shutdown disposes DI-registered services — skip this and buffered log entries in
    /// the APM sink's batch are silently dropped on process exit.
    /// </summary>
    public static ValueTask FlushLogsAsync() => Log.CloseAndFlushAsync();

    /// <summary>
    /// Force-flushes the OTel tracer provider. Only needed by hosts that never call
    /// <c>Run()</c>/<c>RunAsync()</c> — e.g. a Cloud Run *Job* that does one pass and
    /// exits. A long-running host's graceful shutdown already disposes (and thereby
    /// flushes) the DI-registered <see cref="TracerProvider"/> as part of disposing the
    /// host itself; calling this afterward would hit an already-disposed instance. A job
    /// has nothing that triggers that disposal, so this is the only thing standing
    /// between a span and being silently dropped on process exit.
    /// </summary>
    public static void ForceFlushTraces(this IServiceProvider services) =>
        services.GetService<TracerProvider>()?.ForceFlush();

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
