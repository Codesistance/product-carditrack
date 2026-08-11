using Microsoft.Extensions.Configuration;
using Serilog;

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

    public static ILogger CreateLogger(IConfiguration configuration, string applicationName, string serviceName) =>
        new LoggerConfiguration()
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
