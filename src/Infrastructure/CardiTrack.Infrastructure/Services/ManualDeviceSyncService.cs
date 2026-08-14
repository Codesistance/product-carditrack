using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Extensions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <inheritdoc cref="IManualDeviceSyncService"/>
public class ManualDeviceSyncService : IManualDeviceSyncService
{
    /// <summary>
    /// How long a member is off-limits to another manual sync.
    /// </summary>
    /// <remarks>
    /// Provider quotas are per-app, not per-user, so one caregiver leaning on the refresh button
    /// spends everyone's budget. The window is short enough that a caregiver who genuinely wants
    /// fresher data can retry, and the scheduled pull is unaffected either way. See
    /// <see cref="EnforceCooldownAsync"/> for what this does and does not guarantee.
    /// </remarks>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);

    private const string CooldownKeyPrefix = "manualsync:";

    /// <summary>
    /// Ceiling on <see cref="RenewCooldownAsync"/>'s cache write. It runs in a <c>finally</c> on
    /// <see cref="CancellationToken.None"/> by design (see that method's remarks), so without a
    /// bound of its own a stalled cache backend would hang the request indefinitely on the way
    /// out — including a request whose caller already gave up. A cache write is normally
    /// sub-second; this is a backstop, not a realistic budget.
    /// </summary>
    private static readonly TimeSpan RenewalTimeout = TimeSpan.FromSeconds(5);

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;
    private readonly IServiceProvider _services;
    private readonly IDistributedCache _cache;
    private readonly ILogger<ManualDeviceSyncService> _logger;

    public ManualDeviceSyncService(
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access,
        IServiceProvider services,
        IDistributedCache cache,
        ILogger<ManualDeviceSyncService> logger)
    {
        _unitOfWork = unitOfWork;
        _access = access;
        _services = services;
        _cache = cache;
        _logger = logger;
    }

    public async Task<DeviceSyncResultResponse> SyncMemberAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        // View access, not manage: refreshing shows you nothing you couldn't already see, and a
        // relative invited to watch over someone should not be staring at a dead refresh button.
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            throw new KeyNotFoundException("CardiMember not found");

        // Same gate the scheduled pull applies: a paused member's data must not be collected by
        // any path, and a manual sync is still collection.
        if (member.IsMonitoringPaused(DateTime.UtcNow))
        {
            throw new ManualSyncUnavailableException(
                ManualSyncUnavailableException.MonitoringPaused,
                "Monitoring is paused, so we're not collecting new data right now.");
        }

        var connections = (await _unitOfWork.DeviceConnections.GetActiveByCardiMemberIdAsync(cardiMemberId))
            .ToList();
        if (connections.Count == 0)
        {
            throw new ManualSyncUnavailableException(
                ManualSyncUnavailableException.NoConnectedDevice,
                "There's no connected device to check in with yet.");
        }

        await EnforceCooldownAsync(cardiMemberId, ct);

        var outcomes = new List<DeviceSyncOutcome>(connections.Count);
        try
        {
            foreach (var connection in connections)
            {
                // Between devices rather than only up front: a caller who has walked away should
                // not pay for the second provider's round trip.
                ct.ThrowIfCancellationRequested();
                outcomes.Add(await SyncOneAsync(connection, ct));
            }
        }
        finally
        {
            await RenewCooldownAsync(cardiMemberId);
        }

        // Re-read rather than trusting DateTime.UtcNow: SyncCardiMemberAsync only stamps
        // LastSyncDate once the whole window landed, so a failed pull must not advance this.
        var synced = (await _unitOfWork.DeviceConnections.GetActiveByCardiMemberIdAsync(cardiMemberId))
            .Select(c => c.LastSyncDate)
            .Where(d => d is not null)
            .ToList();

        return new DeviceSyncResultResponse
        {
            SyncedCount = outcomes.Count(o => o.Succeeded),
            FailedCount = outcomes.Count(o => !o.Succeeded),
            LastSyncedAt = synced.Count == 0 ? null : synced.Max(),
            Devices = outcomes,
        };
    }

    private async Task<DeviceSyncOutcome> SyncOneAsync(DeviceConnection connection, CancellationToken ct)
    {
        var outcome = new DeviceSyncOutcome
        {
            DeviceId = connection.Id,
            Provider = connection.DeviceType.ToString().ToLowerInvariant(),
        };

        var syncService = _services.GetDeviceSyncService(connection.DeviceType);
        if (syncService is null)
        {
            _logger.LogWarning(
                "No sync service registered for DeviceType {DeviceType}. Skipping DeviceConnection {Id}.",
                connection.DeviceType, connection.Id);
            outcome.Message = "We can't sync this device type yet.";
            return outcome;
        }

        try
        {
            await syncService.SyncCardiMemberAsync(connection);
            outcome.Succeeded = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up. Reporting that as a per-device provider failure would dress an
            // abandoned request up as a 200 and let the remaining devices keep pulling. Note the
            // filter: a provider HTTP timeout also surfaces as TaskCanceledException, and that one
            // genuinely is a provider failure, so it falls through to the handler below.
            throw;
        }
        catch (Exception ex)
        {
            // One failing provider must not sink the others, and the caller still gets a 200
            // carrying the per-device detail. SyncCardiMemberAsync has already moved the
            // connection to SyncError or TokenExpired where that applies.
            _logger.LogError(ex,
                "Manual sync failed for DeviceConnection {Id} (DeviceType={DeviceType}) " +
                "for CardiMember {CardiMemberId}.",
                connection.Id, connection.DeviceType, connection.CardiMemberId);
            outcome.Message = "We couldn't reach this device's provider.";
        }

        return outcome;
    }

    /// <summary>
    /// Claims the member's cooldown slot, throwing if it is already held.
    /// </summary>
    /// <remarks>
    /// A rate limiter, not a mutex, and the distinction is deliberate. The get-then-set is not
    /// atomic — <see cref="IDistributedCache"/> exposes no set-if-absent — so two requests landing
    /// in the same instant can both pass and both pull. What it does stop is the case that
    /// actually happens: a caregiver tapping refresh repeatedly, which is sequential and hits the
    /// claim every time. Losing the race costs one extra pull against a per-app quota measured in
    /// hundreds per hour, which does not justify a Redis <c>SET NX</c> path that only works when
    /// Redis is configured — the cache falls back to in-memory otherwise. Revisit if quota
    /// pressure ever makes the duplicate pull matter.
    /// <para>
    /// The slot is deliberately not released on failure: a provider that just failed is the last
    /// one worth retrying immediately.
    /// </para>
    /// </remarks>
    private async Task EnforceCooldownAsync(Guid cardiMemberId, CancellationToken ct)
    {
        if (await _cache.GetStringAsync(CooldownKeyPrefix + cardiMemberId, ct) is not null)
        {
            throw new ManualSyncUnavailableException(
                ManualSyncUnavailableException.TooSoon,
                "We checked in moments ago — give it a minute before trying again.");
        }

        await SetCooldownAsync(cardiMemberId, ct);
    }

    /// <summary>
    /// Renews the cooldown slot claimed in <see cref="EnforceCooldownAsync"/>.
    /// </summary>
    /// <remarks>
    /// The initial claim is set before the device loop starts, so a slow multi-device sync (or a
    /// hung provider call) can let it expire before the sync itself is done, letting a second
    /// manual sync start concurrently — the opposite failure from the one the "not released on
    /// failure" design above accepts. Called from a <c>finally</c> around that loop so the window
    /// covers the whole sync, not just its start. Uses its own token rather than the caller's: a
    /// cancelled request should not skip protecting quota for the pulls that already went out —
    /// bounded by <see cref="RenewalTimeout"/> rather than left uncancellable, so a stalled cache
    /// backend cannot hang the request on the way out either. A failure here is logged and
    /// swallowed rather than thrown, so it can never mask whatever the try block was already
    /// unwinding for.
    /// </remarks>
    private async Task RenewCooldownAsync(Guid cardiMemberId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(RenewalTimeout);
            await SetCooldownAsync(cardiMemberId, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to renew manual-sync cooldown for CardiMember {CardiMemberId}.", cardiMemberId);
        }
    }

    private Task SetCooldownAsync(Guid cardiMemberId, CancellationToken ct) =>
        _cache.SetStringAsync(
            CooldownKeyPrefix + cardiMemberId,
            "1",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Cooldown },
            ct);
}
