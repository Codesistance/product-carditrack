using CardiTrack.Application.Interfaces.Services;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Runs rule-based device-silence detection every 15 minutes (docs/llm_design.md
/// `InactivityDetector`). Lives here and not in the AI pipeline deliberately: there is no AI
/// call in this path, and non-AI background jobs are Worker-exclusive per CLAUDE.md — the
/// llm_design component table drew it alongside the pipeline, but placement follows the rule,
/// not the diagram.
/// </summary>
public class InactivityDetectionWorker : CronBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<InactivityDetectionOptions> _options;
    private readonly ILogger<InactivityDetectionWorker> _logger;

    public InactivityDetectionWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<InactivityDetectionOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<InactivityDetectionWorker> logger)
        : base(workerOptions.Get(nameof(InactivityDetectionWorker)).CronExpression)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;

        // Bad config must not fault the hosted-service loop (the service also rejects it, but
        // by then the worker would be crash-looping every 15 minutes). Log and sit the run out.
        if (options.SilenceThresholdMinutes <= 0 || options.WakingStartHour < 0
            || options.WakingEndHour > 24 || options.WakingStartHour >= options.WakingEndHour)
        {
            _logger.LogError(
                "InactivityDetection skipped: invalid configuration (SilenceThresholdMinutes=" +
                "{Threshold}, WakingStartHour={Start}, WakingEndHour={End}).",
                options.SilenceThresholdMinutes, options.WakingStartHour, options.WakingEndHour);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var detection = scope.ServiceProvider.GetRequiredService<IInactivityDetectionService>();

        var raised = await detection.DetectAsync(DateTime.UtcNow, new InactivityDetectionRules
        {
            SilenceThresholdMinutes = options.SilenceThresholdMinutes,
            WakingStartHour = options.WakingStartHour,
            WakingEndHour = options.WakingEndHour,
        }, stoppingToken);

        if (raised > 0)
            _logger.LogInformation("InactivityDetection raised {Raised} device-check alert(s).", raised);
    }
}
