using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
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
    /// fresher data can retry, and the scheduled pull is unaffected either way.
    /// </remarks>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);

    private const string CooldownKeyPrefix = "manualsync:";

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
        foreach (var connection in connections)
        {
            outcomes.Add(await SyncOneAsync(connection));
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

    private async Task<DeviceSyncOutcome> SyncOneAsync(DeviceConnection connection)
    {
        var outcome = new DeviceSyncOutcome
        {
            DeviceId = connection.Id,
            Provider = connection.DeviceType.ToString().ToLowerInvariant(),
        };

        var syncService = _services.GetKeyedService<IDeviceSyncService>(connection.DeviceType);
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
    /// Claims the member's cooldown slot, throwing if someone else already holds it.
    /// </summary>
    /// <remarks>
    /// The slot is taken before any pull runs, so two caregivers hitting refresh at once can't
    /// both get through. It is deliberately not released on failure: a provider that just failed
    /// is the last one worth retrying immediately.
    /// </remarks>
    private async Task EnforceCooldownAsync(Guid cardiMemberId, CancellationToken ct)
    {
        var key = CooldownKeyPrefix + cardiMemberId;
        if (await _cache.GetStringAsync(key, ct) is not null)
        {
            throw new ManualSyncUnavailableException(
                ManualSyncUnavailableException.TooSoon,
                "We checked in moments ago — give it a minute before trying again.");
        }

        await _cache.SetStringAsync(
            key,
            "1",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Cooldown },
            ct);
    }
}
