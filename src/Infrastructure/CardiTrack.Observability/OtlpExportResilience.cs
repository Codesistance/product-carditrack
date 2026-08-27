using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.Observability;

/// <summary>
/// Stops OTLP export from quietly losing batches over a healthy network.
/// <para>
/// The failure this exists for is <c>HttpIOException: The response ended prematurely
/// (ResponseEnded)</c>: the intake closed a keep-alive connection on its own idle timer, the
/// pool handed that same connection to the next export, and the batch died on a socket that was
/// already going away. An export is a single request every few seconds against one long-lived
/// host, which is the exact traffic shape that keeps a pooled connection alive right up to the
/// far side's idle limit — so the race is not rare here, it is the normal case. The SDK does not
/// retry by default either, so one lost race is one permanently lost batch of spans.
/// </para>
/// <para>
/// Two settings, one on each side of that: retire pooled connections well before any plausible
/// server-side idle timeout so the race is not run, and turn on the SDK's in-memory retry so
/// losing it once is survivable rather than terminal.
/// </para>
/// </summary>
internal static class OtlpExportResilience
{
    /// <summary>
    /// The names the OTLP exporter resolves from <see cref="IHttpClientFactory"/> for the
    /// http/protobuf transport — one per signal, fixed by the SDK (see
    /// <c>OtlpExporterOptions.HttpClientFactory</c>). Registering them here is what lets this
    /// configure the exporter's transport without replacing the factory delegate itself, and
    /// registering <em>any</em> named client is also what guarantees an
    /// <see cref="IHttpClientFactory"/> is resolvable at all — without one the SDK silently
    /// falls back to instantiating a bare <see cref="HttpClient"/> whose handler nothing here
    /// can reach. Logs ship through Serilog's own OTLP sink, not this transport.
    /// </summary>
    internal const string TraceExporterHttpClientName = "OtlpTraceExporter";

    /// <inheritdoc cref="TraceExporterHttpClientName"/>
    internal const string MetricExporterHttpClientName = "OtlpMetricExporter";

    /// <summary>
    /// The SDK reads its experimental switches through <see cref="IConfiguration"/>, not only
    /// through the process environment, so this can be set in code without an env var.
    /// </summary>
    internal const string RetryConfigurationKey = "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY";

    /// <summary>
    /// In-memory rather than <c>disk</c>: the disk mode needs a writable dedicated directory,
    /// and these hosts run on Cloud Run's ephemeral filesystem where "persisted" telemetry dies
    /// with the instance anyway. In-memory covers what actually happens here — a dropped
    /// connection or a momentary 5xx at the intake — and drops the batch if the process ends
    /// first, which is the same outcome as today, not a worse one.
    /// </summary>
    internal const string InMemoryRetry = "in_memory";

    /// <summary>
    /// Comfortably below any conventional intake idle timeout (typically 60 s or more), so a
    /// connection this pool hands out is one the far side is not about to close.
    /// </summary>
    private static readonly TimeSpan PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Recycles a connection that has stayed busy enough never to go idle, which is
    /// also what picks up a DNS change behind the intake hostname.</summary>
    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Fail a dead route fast; the export is retried, so a long connect wait only
    /// delays that.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Matches the exporter's own default export deadline (<c>TimeoutMilliseconds</c>, 10 s) with
    /// room to spare rather than <see cref="HttpClient"/>'s 100 s default: a factory-created
    /// client does not inherit the exporter's deadline, and a request left running long after the
    /// exporter has given up on it holds a connection for nothing.
    /// </summary>
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registers the transport both OTLP exporters will pick up. Safe to call on a host that
    /// ships nothing — an unused named client costs a registration and no connections.
    /// </summary>
    internal static IServiceCollection AddOtlpExportResilience(this IServiceCollection services)
    {
        foreach (var name in new[] { TraceExporterHttpClientName, MetricExporterHttpClientName })
        {
            services
                .AddHttpClient(name, client => client.Timeout = ExportTimeout)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    PooledConnectionIdleTimeout = PooledConnectionIdleTimeout,
                    PooledConnectionLifetime = PooledConnectionLifetime,
                    ConnectTimeout = ConnectTimeout,
                });
        }

        return services;
    }

    /// <summary>
    /// Turns on in-memory export retry unless the deployment has already said something about it
    /// — an operator who set the key to <c>disk</c>, or to a value that turns it off, is making a
    /// deliberate choice and keeps it. Added as configuration rather than as a process env var so
    /// it cannot leak into anything else the host launches.
    /// </summary>
    internal static void EnableExportRetry(IConfigurationManager configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration[RetryConfigurationKey]))
            return;

        configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { [RetryConfigurationKey] = InMemoryRetry });
    }
}
