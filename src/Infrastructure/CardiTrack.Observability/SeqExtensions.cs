using CardiTrack.Shared;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace CardiTrack.Observability;

/// <summary>
/// Seq is a dev/compose-only sink: like <see cref="ApmExtensions.AddApmShipping"/>,
/// this is a silent no-op unless Serilog:SeqUrl is configured (env var
/// Serilog__SeqUrl wins over appsettings). There is deliberately no fallback URL —
/// an unconfigured environment must not spin a sink that retries against localhost.
/// </summary>
public static class SeqExtensions
{
    public static LoggerConfiguration AddSeqShipping(this LoggerConfiguration loggerConfiguration, IConfiguration configuration)
    {
        var seqUrl = new ConfigurationLoader(configuration).Get(ConfigurationKeys.Serilog.SeqUrl);
        if (string.IsNullOrWhiteSpace(seqUrl))
            return loggerConfiguration;

        return loggerConfiguration.WriteTo.Seq(
            serverUrl: seqUrl,
            restrictedToMinimumLevel: LogEventLevel.Information);
    }
}
