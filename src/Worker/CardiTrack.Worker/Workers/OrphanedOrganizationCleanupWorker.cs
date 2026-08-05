using CardiTrack.Application.Interfaces.Repositories;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Safety net behind the atomic onboarding setup endpoint: removes organizations
/// that still ended up with no user and no CardiMember (e.g. a client that used the
/// legacy two-call flow and died between the calls). The FK cascade deletes their
/// trial subscriptions with them.
/// </summary>
public class OrphanedOrganizationCleanupWorker : CronBackgroundService
{
    // Far longer than any onboarding gap, so an in-flight signup is never swept.
    private static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrphanedOrganizationCleanupWorker> _logger;

    public OrphanedOrganizationCleanupWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<OrphanedOrganizationCleanupWorker> logger)
        : base(options.Get(nameof(OrphanedOrganizationCleanupWorker)).CronExpression)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OrphanedOrganizationCleanup triggered at {Time}", DateTime.UtcNow);

        using var scope = _scopeFactory.CreateScope();
        var organizations = scope.ServiceProvider.GetRequiredService<IOrganizationRepository>();

        var removed = await organizations.DeleteOrphanedAsync(MinAge);

        if (removed > 0)
        {
            // Warning: orphans mean some client bypassed the atomic setup endpoint
            // and failed mid-onboarding — worth investigating, not just cleaning.
            _logger.LogWarning(
                "OrphanedOrganizationCleanup removed {Count} organizations older than {MinAge} with no users or CardiMembers.",
                removed, MinAge);
        }
        else
        {
            _logger.LogInformation("OrphanedOrganizationCleanup complete. Nothing to remove.");
        }
    }
}
