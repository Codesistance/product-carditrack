using CardiTrack.Application.Interfaces.Services;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Tells families the good news: once a day, finds the members nothing has been raised about for
/// a week or more and pushes them an all-clear.
/// </summary>
/// <remarks>
/// <para>
/// Pure rule evaluation against data already in the database — no AI call — which is why it lives
/// in the Worker per CLAUDE.md, alongside <see cref="StatisticalAlertWorker"/> whose candidate
/// filter and per-member isolation it mirrors.
/// </para>
/// <para>
/// <b>Daily, but paced weekly.</b> The cadence a caregiver experiences is one all-clear a week per
/// member, held by the dedup key <c>DispatchService.EnqueueForReassuranceAsync</c> derives from
/// the stretch's week number. The sweep runs daily anyway so that a member crossing the seven-day
/// line on a Tuesday is reported on the Tuesday, rather than waiting for whichever weekday a
/// weekly job was scheduled on — which for the first message, the one that answers "has this
/// thing been working at all", is most of its value.
/// </para>
/// <para>
/// <b>Runs after <see cref="BaselineCalculationWorker"/>.</b> The established 30-day baseline is
/// the precondition the whole feature rests on, so this reads that morning's numbers rather than
/// yesterday's — the same ordering <see cref="DataCompletenessWorker"/> keeps, and for the same
/// reason.
/// </para>
/// </remarks>
public class QuietReassuranceWorker : CronBackgroundService
{
    /// <summary>
    /// Arbitrary but fixed, and distinct from every other job's — Postgres namespaces advisory
    /// locks by the number alone. See <see cref="AdvisoryLock"/>.
    /// </summary>
    private const long AdvisoryLockKey = 8_472_100_002;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QuietReassuranceWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public QuietReassuranceWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<QuietReassuranceWorker> logger,
        TimeProvider? timeProvider = null)
        : base(options.Get(nameof(QuietReassuranceWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        // Locked for the same reason DataCompletenessWorker is, not for correctness: the dedup
        // key already makes a repeated pass a no-op, so this only stops three instances scanning
        // the whole estate for one result.
        var ran = await AdvisoryLock.TryRunAsync(
            _scopeFactory, AdvisoryLockKey, SweepAsync, stoppingToken);

        if (!ran)
        {
            _logger.LogInformation(
                "QuietReassurance skipped — another instance holds the advisory lock.");
        }

        async Task SweepAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var sweep = scope.ServiceProvider.GetRequiredService<IQuietReassuranceService>();
            await sweep.SweepAsync(_timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
        }
    }
}
