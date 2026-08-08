using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Serilog setup for the app: rolling log files in the app's data directory (all builds)
/// plus IDE debug output (Debug builds), and last-chance handlers so crashes are written
/// to the log before the process dies.
/// </summary>
public static class AppLogging
{
    /// <summary>Where log files land — surfaced so a support/diagnostics screen can find them.</summary>
    public static string LogDirectory => Path.Combine(FileSystem.AppDataDirectory, "logs");

    public static void Configure(ILoggingBuilder logging)
    {
        var config = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
            .WriteTo.Debug(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
#else
            .MinimumLevel.Information()
#endif
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            // Which build wrote the line. Files roll daily and by size, so a support
            // bundle can arrive without whatever startup line named the version —
            // stamping every line keeps each file self-describing. VersionString is
            // ApplicationDisplayVersion, which the signed CI builds set from the
            // release tag; unstamped local builds report the csproj default.
            .Enrich.WithProperty("Version", AppInfo.Current.VersionString)
            .WriteTo.File(
                path: Path.Combine(LogDirectory, "carditrack-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] v{Version} {SourceContext}: {Message:lj}{NewLine}{Exception}");

        Log.Logger = config.CreateLogger();
        logging.AddSerilog(Log.Logger, dispose: true);
    }

    /// <summary>
    /// Catches exceptions that escape every page/handler. These would otherwise kill the
    /// process (or vanish, for unobserved tasks) with nothing written locally.
    /// </summary>
    public static void HookUnhandledExceptions(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CardiTrack.Mobile.Unhandled");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                logger.LogCritical(ex, "Unhandled exception (terminating: {IsTerminating})", e.IsTerminating);
            else
                logger.LogCritical("Unhandled non-exception object: {ExceptionObject} (terminating: {IsTerminating})",
                    e.ExceptionObject, e.IsTerminating);

            // The process is about to die — make sure the file sink is flushed.
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }
}
