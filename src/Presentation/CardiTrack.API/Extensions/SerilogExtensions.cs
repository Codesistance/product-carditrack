using CardiTrack.Observability;
using Serilog;

namespace CardiTrack.API.Extensions;

public static class SerilogExtensions
{
    public static WebApplicationBuilder AddSerilogLogging(this WebApplicationBuilder builder)
    {
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentName()
            .Enrich.WithProperty("Application", "CardiTrack.API")
            .Enrich.WithProperty("Version", DeploymentInfo.Version)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
            .AddApmShipping(builder.Configuration.GetApmOptions())
            .CreateLogger();

        builder.Host.UseSerilog();

        return builder;
    }
}
