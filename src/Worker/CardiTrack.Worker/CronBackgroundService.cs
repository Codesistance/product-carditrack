using Cronos;

namespace CardiTrack.Worker;

public abstract class CronBackgroundService : BackgroundService
{
    private readonly CronExpression _cron;
    private readonly TimeZoneInfo _timeZone;
    private readonly bool _runOnStartup;
    private readonly ILogger? _logger;

    protected CronBackgroundService(
        string cronExpression,
        TimeZoneInfo? timeZone = null,
        bool runOnStartup = false,
        ILogger? logger = null)
    {
        _cron = CronExpression.Parse(cronExpression, CronFormat.IncludeSeconds);
        _timeZone = timeZone ?? TimeZoneInfo.Utc;
        _runOnStartup = runOnStartup;
        _logger = logger;
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
                _logger?.LogError(ex,
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
                await ExecuteJobAsync(stoppingToken);
        }
    }

    protected abstract Task ExecuteJobAsync(CancellationToken stoppingToken);
}
