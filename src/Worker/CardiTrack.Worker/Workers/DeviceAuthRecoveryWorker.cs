using CardiTrack.Application.Interfaces.Services;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Retries the refresh token of device connections the provider has refused, so one that can come
/// back does so on its own (see <c>DeviceAuthRecoveryService</c>).
/// </summary>
/// <remarks>
/// Fifteen minutes is the pass cadence, not the retry cadence — each connection carries its own
/// widening backoff, so a pass mostly finds nothing due. Worker-hosted per CLAUDE.md: no AI call
/// is involved, and DB polling belongs here.
/// </remarks>
public class DeviceAuthRecoveryWorker : CronBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeviceAuthRecoveryWorker> _logger;

    public DeviceAuthRecoveryWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<DeviceAuthRecoveryWorker> logger)
        : base(workerOptions.Get(nameof(DeviceAuthRecoveryWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var recovery = scope.ServiceProvider.GetRequiredService<IDeviceAuthRecoveryService>();

        var recovered = await recovery.RecoverDueConnectionsAsync(DateTime.UtcNow, stoppingToken);

        if (recovered > 0)
            _logger.LogInformation("Device auth recovery returned {Recovered} connection(s) to service.", recovered);
    }
}
