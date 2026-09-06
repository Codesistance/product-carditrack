using CardiTrack.Application.Interfaces.Services;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Runs the user-defined alarm engine every five minutes — threshold arithmetic against the
/// member's own readings, no AI call, which is why it lives in the Worker per CLAUDE.md.
/// <para>
/// Five minutes rather than the statistical engine's fifteen because these alarms are the ones a
/// caregiver sets themselves, and the shortest period the catalogue offers is five: a cadence
/// slower than the period would quietly make a "tell me within five minutes" alarm mean something
/// else. It is deliberately no faster, either — ingestion polls every ten minutes, so a tighter
/// loop would only re-read the same data.
/// </para>
/// </summary>
public class MetricAlarmWorker : CronBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MetricAlarmWorker> _logger;

    public MetricAlarmWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<MetricAlarmWorker> logger)
        : base(workerOptions.Get(nameof(MetricAlarmWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IMetricAlarmEngine>();

        var raised = await engine.EvaluateAsync(DateTime.UtcNow, stoppingToken);
        if (raised > 0)
            _logger.LogInformation("MetricAlarm pass raised {Raised} alert(s).", raised);
    }
}
