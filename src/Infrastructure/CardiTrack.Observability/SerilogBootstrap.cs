using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Debugging;

namespace CardiTrack.Observability;

/// <summary>
/// The one Serilog bootstrap chain every host builds its pre-DI logger from — console always,
/// plus APM shipping when the Apm engine is configured (<see cref="ApmExtensions.AddApmShipping"/>).
/// Was duplicated near-verbatim across all five hosts (only the application name and
/// <see cref="ApmServiceNames"/> constant differed); centralized here so a change to the
/// enrichment chain — like <see cref="ActivityLogEnricher"/> — applies to every host at once,
/// matching how <see cref="ApmExtensions.AddApmTracing"/> already centralizes the OTel side.
/// </summary>
public static class SerilogBootstrap
{
    private const string ConsoleOutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";

    public static ILogger CreateLogger(IConfiguration configuration, string applicationName, string serviceName)
    {
        // Serilog reports its own failures — a sink that could not emit a batch, and gave up on
        // it — only through SelfLog, which writes nowhere until something enables it. Nothing
        // did, so a batch the OTLP sink dropped left no trace anywhere: not in Datadog, where it
        // never arrived, and not in Cloud Logging, where the sink never said so. Stderr is
        // captured by Cloud Run as an error-severity line, which is what a dropped batch is.
        // OtlpExportDiagnostics already writes the SDK's export failures to the same place.
        SelfLog.Enable(Console.Error);

        return new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .Enrich.With(new ActivityLogEnricher())
            .Enrich.WithProperty("Application", applicationName)
            .Enrich.WithProperty("Version", DeploymentInfo.Version)
            .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
            .AddApmShipping(configuration.GetApmOptions(), serviceName)
            .CreateLogger();
    }
}
