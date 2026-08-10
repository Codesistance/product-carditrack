using CardiTrack.Observability;
using Serilog;

namespace CardiTrack.API.Extensions;

public static class SerilogExtensions
{
    public static WebApplicationBuilder AddSerilogLogging(this WebApplicationBuilder builder)
    {
        Log.Logger = SerilogBootstrap.CreateLogger(builder.Configuration, "CardiTrack.API", ApmServiceNames.Api);

        builder.Host.UseSerilog();

        return builder;
    }
}
