using Cronos;

namespace CardiTrack.Worker;

public abstract class CronBackgroundService : BackgroundService
{
    private readonly CronExpression _cron;
    private readonly TimeZoneInfo _timeZone;
    private readonly bool _runOnStartup;
    private readonly ILogger _logger;

    /// <remarks>
    /// <paramref name="logger"/> is required rather than optional because both invocation paths
    /// below swallow job exceptions to keep the host alive — with no logger, a permanently failing
    /// job would fail silently forever, which is worse than the crash it prevents.
    /// </remarks>
    protected CronBackgroundService(
        string cronExpression,
        ILogger logger,
        TimeZoneInfo? timeZone = null,
        bool runOnStartup = false)
    {
        _cron = CronExpression.Parse(cronExpression, CronFormat.IncludeSeconds);
        _timeZone = timeZone ?? TimeZoneInfo.Utc;
        _runOnStartup = runOnStartup;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runOnStartup && !stoppingToken.IsCancellationRequested)
        {
            // Unlike a scheduled tick, this fires the moment the process comes up — exactly when
            // a dependency (e.g. Cloud SQL, mid-contention with other services cold-starting at
            // once) is least likely to be ready. Left unguarded, an unhandled exception here would
            // fault ExecuteAsync at boot, and this host has no BackgroundServiceExceptionBehavior
            // override, so the default (StopHost) would take down the whole worker process on
            // every cold start — worse than the silent gap this run-on-startup mode exists to fix.
            try
            {
                await ExecuteJobAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Run-on-startup invocation of {Job} failed; it will still run on its next scheduled tick.",
                    GetType().Name);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var next = _cron.GetNextOccurrence(now, _timeZone);

            if (next is null)
                break;

            var delay = next.Value - now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);

            if (!stoppingToken.IsCancellationRequested)
            {
                // Same guard, same reason as the run-on-startup call above: with StopHost in
                // effect, one unhandled exception here faults ExecuteAsync and takes the entire
                // worker process down — every other job with it. A job that throws should miss
                // its own tick, not end the host (incident 2026-08-12).
                try
                {
                    await ExecuteJobAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "Scheduled invocation of {Job} failed; it will run again on its next tick.",
                        GetType().Name);
                }
            }
        }
    }

    protected abstract Task ExecuteJobAsync(CancellationToken stoppingToken);
}
