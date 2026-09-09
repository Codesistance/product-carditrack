using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Executes caregiver-requested history re-pulls (<see cref="DeviceHistoryRepull"/>): the API
/// records the request, and this is the only thing that fetches for it.
/// </summary>
/// <remarks>
/// <para>
/// One chunk per request per tick, newest days first, so a 90-day request costs ~13 ticks
/// rather than one enormous pull that would trip the wearer's per-minute ceiling partway and
/// have to start over. Progress is the oldest day reached, written after every chunk, so a
/// crash mid-chunk re-fetches at most that chunk — the day upserts are idempotent.
/// </para>
/// <para>
/// A provider failure counts an attempt and leaves the request open for the next tick; the
/// request fails only once <see cref="MaxAttempts"/> chunk attempts have failed, and what was
/// stored before stays stored. It never moves the connection to <c>SyncError</c> — a day the
/// provider refuses two months back is not evidence about the connection today, and the routine
/// sync is what decides that (see <see cref="IDeviceSyncService.PullHistoryRangeAsync"/>).
/// </para>
/// <para>
/// Under an advisory lock for restraint, like the daily sweeps: two instances draining the same
/// requests would double the quota spend for no extra progress. Offset from
/// <see cref="WearableSyncWorker"/>'s minute so a wearer never pays for a routine pull and a
/// chunk in the same sixty seconds.
/// </para>
/// </remarks>
public class HistoryRepullWorker : CronBackgroundService
{
    /// <summary>
    /// Fixed per job; see <see cref="AdvisoryLock"/> for the keys already spoken for.
    /// </summary>
    private const long AdvisoryLockKey = 8_472_100_004;

    /// <summary>
    /// Failed chunk attempts before a request is given up on. Three ticks is half an hour of
    /// the provider refusing — past the transient blips the HTTP retry handler already absorbs,
    /// and short enough that a caregiver watching the card learns the outcome the same hour.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>Fallback chunk size when the provider block does not say.</summary>
    private const int DefaultChunkDays = 7;

    private readonly IOptionsMonitor<HistoryRepullOptions> _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HistoryRepullWorker> _logger;
    private readonly TimeProvider _time;

    public HistoryRepullWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<HistoryRepullOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<HistoryRepullWorker> logger,
        TimeProvider? timeProvider = null)
        : base(workerOptions.Get(nameof(HistoryRepullWorker)).CronExpression, logger)
    {
        _options = options;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        var ran = await AdvisoryLock.TryRunAsync(
            _scopeFactory, AdvisoryLockKey, () => SweepAsync(stoppingToken), stoppingToken);

        if (!ran)
            _logger.LogInformation("HistoryRepull skipped — another instance holds the advisory lock.");
    }

    /// <summary>One tick: advance up to <see cref="HistoryRepullOptions.MaxPerTick"/> open requests by one chunk each.</summary>
    protected async Task SweepAsync(CancellationToken ct)
    {
        var maxPerTick = _options.CurrentValue.MaxPerTick;
        if (maxPerTick <= 0)
        {
            _logger.LogInformation("HistoryRepull skipped — MaxPerTick is not positive.");
            return;
        }

        List<Guid> dueIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            dueIds = (await unitOfWork.DeviceHistoryRepulls.GetDueAsync(maxPerTick, ct))
                .Select(r => r.Id)
                .ToList();
        }

        if (dueIds.Count == 0)
            return;

        _logger.LogInformation("HistoryRepull triggered at {Time} with {Count} open request(s).",
            _time.GetUtcNow(), dueIds.Count);

        var advanced = 0;
        var completed = 0;
        var cancelled = 0;
        var failed = 0;
        var retrying = 0;

        foreach (var repullId in dueIds)
        {
            if (ct.IsCancellationRequested)
                break;

            switch (await AdvanceAsync(repullId, ct))
            {
                case Outcome.Advanced: advanced++; break;
                case Outcome.Completed: completed++; break;
                case Outcome.Cancelled: cancelled++; break;
                case Outcome.Failed: failed++; break;
                case Outcome.Retrying: retrying++; break;
            }
        }

        _logger.LogInformation(
            "HistoryRepull complete. Advanced: {Advanced}, completed: {Completed}, cancelled: {Cancelled}, " +
            "failed: {Failed}, retrying: {Retrying}.",
            advanced, completed, cancelled, failed, retrying);
    }

    /// <summary>
    /// One scope — and so one DbContext — per request: a chunk tracks a week of raw rows, merges
    /// and hour vectors, and one request's failure must not take the rest of the tick with it.
    /// </summary>
    private async Task<Outcome> AdvanceAsync(Guid repullId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var now = _time.GetUtcNow().UtcDateTime;

        // Re-read under this scope rather than carrying the row across: it was read tracked by
        // a context that is gone, and the API may have moved it since the due query.
        var repull = await unitOfWork.DeviceHistoryRepulls.GetByIdAsync(repullId);
        if (repull is null || !repull.IsOpen)
            return Outcome.Skipped;

        var connection = await unitOfWork.DeviceConnections.GetByIdAsync(repull.DeviceConnectionId);
        if (connection is null || !connection.IsActive
            || connection.ConnectionStatus is not (ConnectionStatus.Connected or ConnectionStatus.SyncError))
        {
            return await CloseAsync(unitOfWork, repull, HistoryRepullStatus.Cancelled,
                HistoryRepullReasons.DeviceNotSyncable, now, Outcome.Cancelled);
        }

        var member = await unitOfWork.CardiMembers.GetByIdAsync(repull.CardiMemberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(now))
        {
            // The API refuses a request while paused; this covers a pause that began after the
            // request was queued. Same rule as every collection path: paused means not collected.
            return await CloseAsync(unitOfWork, repull, HistoryRepullStatus.Cancelled,
                HistoryRepullReasons.MonitoringPaused, now, Outcome.Cancelled);
        }

        var syncService = scope.ServiceProvider.GetDeviceSyncService(connection.DeviceType);
        if (syncService is null)
        {
            _logger.LogWarning(
                "No provider config or sync service for DeviceType {DeviceType}; failing re-pull {RepullId}.",
                connection.DeviceType, repull.Id);
            return await CloseAsync(unitOfWork, repull, HistoryRepullStatus.Failed,
                HistoryRepullReasons.NoSyncService, now, Outcome.Failed);
        }

        var providers = scope.ServiceProvider.GetRequiredService<IOptions<List<DeviceProviderSettings>>>().Value;
        var chunkDays = Math.Max(1, providers.ConfigFor(connection.DeviceType)?.BackfillChunkDays ?? DefaultChunkDays);

        if (repull.Status == HistoryRepullStatus.Pending)
        {
            // Visible as running before the first provider call, so a crash leaves a row that
            // says "started" rather than one that looks untouched.
            repull.Status = HistoryRepullStatus.InProgress;
            repull.StartedAt = now;
            repull.UpdatedDate = now;
            unitOfWork.DeviceHistoryRepulls.Update(repull);
            await unitOfWork.SaveChangesAsync();
        }

        var (from, to) = HistoryRepullWindow.NextChunk(repull, chunkDays);

        try
        {
            // A day two months back may never have had its granular partition created — nothing
            // was syncing then. Idempotent, and required rather than optional: this job only
            // runs in the Worker, which registers the port, and a host that somehow lacks it
            // should say so here rather than let the chunk fail on a missing-partition insert
            // several requests deep.
            var partitions = scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>();
            await partitions.EnsurePartitionsForRangeAsync(from, to, ct);

            var daysWithData = await syncService.PullHistoryRangeAsync(connection, from, to, ct);

            repull.CompletedTo = from;
            repull.DaysWithData += daysWithData;
            repull.UpdatedDate = _time.GetUtcNow().UtcDateTime;

            // A chunk that landed clears what the retried ones left behind. Otherwise a request
            // that stumbled once and then finished carries the reason it stumbled for good, and
            // reads afterwards as a completed request that also failed. The attempt count goes
            // with it: it is the run-up to giving up, and progress ended that run-up.
            repull.FailureReason = null;
            repull.Attempts = 0;

            var done = HistoryRepullWindow.IsComplete(repull);
            if (done)
            {
                repull.Status = HistoryRepullStatus.Completed;
                repull.CompletedAt = repull.UpdatedDate;
            }

            unitOfWork.DeviceHistoryRepulls.Update(repull);
            await unitOfWork.SaveChangesAsync();

            _logger.LogInformation(
                "Re-pull {RepullId} fetched {From}..{To} for DeviceConnection {DeviceConnectionId} " +
                "({DaysWithData} day(s) with data); {Progress}/{Total} days done{Completed}.",
                repull.Id, from, to, connection.Id, daysWithData,
                HistoryRepullWindow.DaysDone(repull), HistoryRepullWindow.TotalDays(repull),
                done ? " — complete" : string.Empty);

            return done ? Outcome.Completed : Outcome.Advanced;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutting down: the row stays InProgress and the next tick re-fetches the chunk.
            throw;
        }
        catch (Exception ex)
        {
            repull.Attempts++;
            repull.FailureReason = HistoryRepullReasons.From(ex);
            repull.UpdatedDate = _time.GetUtcNow().UtcDateTime;

            var givingUp = repull.Attempts >= MaxAttempts;
            if (givingUp)
            {
                repull.Status = HistoryRepullStatus.Failed;
                repull.CompletedAt = repull.UpdatedDate;
            }

            unitOfWork.DeviceHistoryRepulls.Update(repull);
            await unitOfWork.SaveChangesAsync();

            // Ids only — a provider response can carry the wearer's readings, and the reason
            // stored on the row is already the type name, not the body.
            _logger.LogError(ex,
                "Re-pull {RepullId} chunk {From}..{To} failed for DeviceConnection {DeviceConnectionId} " +
                "(attempt {Attempt} of {MaxAttempts}){GivingUp}.",
                repull.Id, from, to, connection.Id, repull.Attempts, MaxAttempts,
                givingUp ? " — giving up" : string.Empty);

            return givingUp ? Outcome.Failed : Outcome.Retrying;
        }
    }

    private static async Task<Outcome> CloseAsync(
        IUnitOfWork unitOfWork, DeviceHistoryRepull repull, HistoryRepullStatus status,
        string reason, DateTime now, Outcome outcome)
    {
        repull.Status = status;
        repull.FailureReason = reason;
        repull.CompletedAt = now;
        repull.UpdatedDate = now;
        unitOfWork.DeviceHistoryRepulls.Update(repull);
        await unitOfWork.SaveChangesAsync();
        return outcome;
    }

    private enum Outcome
    {
        Skipped,
        Advanced,
        Completed,
        Cancelled,
        Failed,
        Retrying,
    }

    /// <summary>
    /// The payload-free labels stored in <see cref="DeviceHistoryRepull.FailureReason"/>. Codes
    /// where the API has one, an exception's type name (plus the HTTP status for a provider
    /// refusal) otherwise — never a message, which for a provider error can carry readings.
    /// </summary>
    internal static class HistoryRepullReasons
    {
        public const string DeviceNotSyncable = "DEVICE_NOT_SYNCABLE";
        public const string MonitoringPaused = "MONITORING_PAUSED";
        public const string NoSyncService = "NO_SYNC_SERVICE";

        public static string From(Exception ex)
        {
            var reason = ex is GoogleHealthApiException api
                ? $"{ex.GetType().Name} (HTTP {(int)api.StatusCode})"
                : ex.GetType().Name;
            return reason.Length <= 200 ? reason : reason[..200];
        }
    }
}
