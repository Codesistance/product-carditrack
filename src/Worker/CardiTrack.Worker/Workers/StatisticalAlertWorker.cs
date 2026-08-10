using CardiTrack.Application.Interfaces.Services;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Runs the R1 statistical alert engine every 15 minutes — pure rule evaluation against
/// established baselines, no AI call, which is why it lives in the Worker per CLAUDE.md.
/// The 15-minute cadence exists for the one intraday rule (`no_morning_activity`); the
/// daily-grain rules are held to once per day by the service's same-local-day dedup, so the
/// frequent cadence costs nothing but reads.
/// </summary>
public class StatisticalAlertWorker : CronBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StatisticalAlertWorker> _logger;

    public StatisticalAlertWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<StatisticalAlertWorker> logger)
        : base(workerOptions.Get(nameof(StatisticalAlertWorker)).CronExpression)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IStatisticalAlertService>();

        var raised = await engine.EvaluateAsync(DateTime.UtcNow, stoppingToken);
        if (raised > 0)
            _logger.LogInformation("StatisticalAlert pass raised {Raised} alert(s).", raised);
    }
}
