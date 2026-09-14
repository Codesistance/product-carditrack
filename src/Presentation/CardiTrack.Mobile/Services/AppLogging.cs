using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Mobile.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Serilog setup for the app: rolling log files in the app's data directory (all builds)
/// plus IDE debug output (Debug builds), a relay that gets Error-and-above lines to Datadog
/// through the API (stamped builds only), and last-chance handlers so crashes are written
/// to the log — and handed to the relay — before the process dies.
/// </summary>
public static class AppLogging
{
    /// <summary>Where log files land — surfaced so a support/diagnostics screen can find them.</summary>
    public static string LogDirectory => Path.Combine(FileSystem.AppDataDirectory, "logs");

    /// <summary>
    /// The unhandled handler gets this long to send the crash before the runtime aborts. Long
    /// enough for one small POST on a live connection, short enough that a phone with no
    /// network is not held in a frozen app noticeably longer than the crash itself takes.
    /// </summary>
    private static readonly TimeSpan CrashFlushBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The error-log relay. Disabled (every call a no-op) in a build with no
    /// <see cref="AppConfig.MobileDiagnosticsKey"/>; created in <see cref="Configure"/>.
    /// </summary>
    public static IMobileDiagnosticsRelay Diagnostics { get; private set; } = DisabledRelay.Instance;

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
            // Which build wrote the line. Files roll daily, so a support bundle can
            // arrive without whatever startup line named the version —
            // stamping every line keeps each file self-describing. VersionString is
            // ApplicationDisplayVersion, which the signed CI builds set from the
            // release tag; unstamped local builds report the csproj default.
            .Enrich.WithProperty("Version", AppInfo.Current.VersionString)
            .WriteTo.File(
                path: Path.Combine(LogDirectory, "carditrack-.log"),
                restrictedToMinimumLevel: LogEventLevel.Warning,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] v{Version} {SourceContext}: {Message:lj}{NewLine}{Exception}");

        // Error and above, not Warning like the file: the file is the caregiver's to share, the
        // relay sends unprompted, so it carries the least that still names a crash. Queued
        // here, sent by FlushDiagnostics and the unhandled handler below. Not gated on the
        // Send-diagnostics toggle — that governs the Datadog SDK's session telemetry, and the
        // crash this exists for happens on the sign-in screen, before the toggle can be reached
        // (docs/compliance/dpia.md A9).
        Diagnostics = CreateRelay();
        config.WriteTo.Sink(new MobileDiagnosticsSink(Diagnostics), restrictedToMinimumLevel: LogEventLevel.Error);

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

            // The process is about to die. The relay first, while the network is still there
            // to use — the runtime aborts the moment these handlers return, and a crash that
            // only reaches the file is a crash nobody sees until the phone is in someone's hand.
            // Whatever does not make it in the budget is still queued for the next launch.
            Diagnostics.TryFlushBlocking(CrashFlushBudget);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    /// <summary>
    /// Sends whatever the relay has queued, off the calling thread and without waiting. Called
    /// once the app is built (for the previous run's crash) and on every resume.
    /// </summary>
    public static void FlushDiagnostics()
    {
        if (!Diagnostics.Enabled)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Diagnostics.FlushAsync();
            }
            catch (Exception)
            {
                // FlushAsync swallows its own failures; this is belt and braces for a
                // fire-and-forget task, which must never become an unobserved exception.
            }
        });
    }

    /// <summary>
    /// A plain HttpClient of its own rather than the DI-built API client: this has to exist
    /// before the container does (Serilog is configured first), must never carry a bearer
    /// token (the endpoint is anonymous by design), and must never retry through the auth
    /// handler on the way to reporting that the auth handler threw.
    /// </summary>
    private static IMobileDiagnosticsRelay CreateRelay()
    {
        if (string.IsNullOrWhiteSpace(AppConfig.MobileDiagnosticsKey))
            return DisabledRelay.Instance;

        var http = new HttpClient
        {
            BaseAddress = new Uri(AppConfig.ApiBaseUrl),
            Timeout = TimeSpan.FromSeconds(15),
        };
        ClientIdentity.Apply(http.DefaultRequestHeaders);

        return new MobileDiagnosticsRelay(
            http,
            AppConfig.MobileDiagnosticsKey,
            Path.Combine(LogDirectory, "diagnostics-queue.jsonl"),
            MobileDiagnosticsContext.DescribeDevice);
    }

    /// <summary>The relay a build without a key gets: nothing is queued, nothing is sent.</summary>
    private sealed class DisabledRelay : IMobileDiagnosticsRelay
    {
        public static DisabledRelay Instance { get; } = new();

        public bool Enabled => false;

        public void Record(MobileDiagnosticsLogEntry entry)
        {
        }

        public Task<bool> FlushAsync(CancellationToken ct = default) => Task.FromResult(false);

        public bool TryFlushBlocking(TimeSpan budget) => false;
    }
}
