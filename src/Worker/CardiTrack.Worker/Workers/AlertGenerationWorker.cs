using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Alerts;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Evaluates the five statistical alert rules against every member with an established baseline,
/// and writes the alerts they raise.
/// </summary>
/// <remarks>
/// <para>
/// This is the job that makes the rest of the alerting stack mean anything: <c>AlertsController</c>
/// and the M1-10 list have been serving an empty table because nothing created a row.
/// </para>
/// <para>
/// Hourly rather than daily. Four of the five rules read whole days and would be happy overnight,
/// but "no movement by 11am" is only true at 11am — and 11am arrives at a different moment for
/// every time zone, so the check has to come round often enough to catch each one.
/// </para>
/// </remarks>
public class AlertGenerationWorker : CronBackgroundService
{
    /// <summary>Distinct from the completeness worker's key; the two must never block each other.</summary>
    private const long AdvisoryLockKey = 8_472_100_002;

    private const int BatchSize = 100;

    /// <summary>
    /// Only members whose device reported inside this window are worth evaluating. A member with
    /// no recent data has a device problem, which is a notification rather than a health alert.
    /// </summary>
    private const int RecentActivityDays = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AlertGenerationWorker> _logger;

    public AlertGenerationWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<AlertGenerationWorker> logger)
        : base(options.Get(nameof(AlertGenerationWorker)).CronExpression)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockContext = lockScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var connection = lockContext.Database.GetDbConnection();
        await connection.OpenAsync(stoppingToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_try_advisory_lock({AdvisoryLockKey});";
        var acquired = (bool?)await command.ExecuteScalarAsync(stoppingToken) ?? false;

        if (!acquired)
        {
            // Two instances generating the same alerts would double-notify a family. The dedup in
            // AlertGenerator reads state this run has not written yet, so the lock is what makes
            // "one open alert per type" hold across instances rather than only within one.
            _logger.LogInformation(
                "AlertGeneration skipped — another instance holds the advisory lock.");
            return;
        }

        try
        {
            await RunAsync(stoppingToken);
        }
        finally
        {
            await using var unlock = connection.CreateCommand();
            unlock.CommandText = $"SELECT pg_advisory_unlock({AdvisoryLockKey});";
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-RecentActivityDays);
        var evaluated = 0;
        var raised = 0;
        var failed = 0;

        Guid? cursor = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            List<Guid> memberIds;
            using (var scope = _scopeFactory.CreateScope())
            {
                var snapshots = scope.ServiceProvider.GetRequiredService<IAlertSnapshotQueries>();
                memberIds = [.. await snapshots.GetMembersDueForEvaluationAsync(
                    since, cursor, BatchSize, stoppingToken)];
            }

            if (memberIds.Count == 0)
                break;

            foreach (var memberId in memberIds)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    raised += await EvaluateMemberAsync(memberId, stoppingToken);
                    evaluated++;
                }
                catch (Exception ex)
                {
                    // One member's bad data must not cost everybody else their alerts.
                    failed++;
                    _logger.LogError(ex,
                        "Failed to evaluate alerts for CardiMember {CardiMemberId}.", memberId);
                }

                cursor = memberId;
            }
        }

        _logger.LogInformation(
            "AlertGeneration complete. Members evaluated: {Evaluated}, alerts raised: {Raised}, failed: {Failed}.",
            evaluated, raised, failed);
    }

    /// <summary>
    /// One scope per member: the snapshot tracks a month of day rows and today's hourly series,
    /// which would accumulate across the estate on a shared context.
    /// </summary>
    private async Task<int> EvaluateMemberAsync(Guid cardiMemberId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<IAlertSnapshotQueries>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var context = await snapshots.BuildContextAsync(cardiMemberId, DateTime.UtcNow, ct);
        if (context is null)
            return 0;

        var alerts = AlertGenerator.Generate(context);
        if (alerts.Count == 0)
            return 0;

        await unitOfWork.Alerts.AddRangeAsync(alerts);
        await unitOfWork.SaveChangesAsync();

        foreach (var alert in alerts)
        {
            // Logged at Information with the type and severity but no metric values — an alert
            // body describes someone's health, and logs are not the place for it.
            _logger.LogInformation(
                "Raised {Severity} {AlertType} alert for CardiMember {CardiMemberId}.",
                alert.Severity, alert.AlertType, cardiMemberId);
        }

        return alerts.Count;
    }
}
