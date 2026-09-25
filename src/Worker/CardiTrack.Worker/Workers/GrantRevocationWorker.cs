using CardiTrack.Application.Interfaces.Services;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Ends the provider grants of removed and replaced devices, and of grants refused after the code
/// exchange, from the queue the device flows write in the same transaction that discards the tokens
/// (see <c>GrantRevocationService</c>).
/// </summary>
/// <remarks>
/// Every minute, so a removed device leaves the wearer's list of apps with access to their health
/// data about as soon as it would have when the request revoked it inline — without a provider
/// timeout or a cancelled request being able to lose it. Worker-hosted per CLAUDE.md: no AI call
/// is involved, and DB polling belongs here.
/// </remarks>
public class GrantRevocationWorker : CronBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GrantRevocationWorker> _logger;

    public GrantRevocationWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<GrantRevocationWorker> logger)
        : base(workerOptions.Get(nameof(GrantRevocationWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var revocations = scope.ServiceProvider.GetRequiredService<IGrantRevocationService>();

        var ended = await revocations.RevokeDueAsync(DateTime.UtcNow, stoppingToken);

        if (ended > 0)
            _logger.LogInformation("Ended {Ended} provider grant(s).", ended);
    }
}
