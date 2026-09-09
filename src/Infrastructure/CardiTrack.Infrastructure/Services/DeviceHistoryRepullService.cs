using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CardiTrack.Infrastructure.Services;

/// <inheritdoc cref="IDeviceHistoryRepullService"/>
public class DeviceHistoryRepullService : IDeviceHistoryRepullService
{
    /// <summary>PostgreSQL's unique-violation SQLSTATE — the partial index closing the insert race.</summary>
    private const string UniqueViolation = "23505";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;
    private readonly List<DeviceProviderSettings> _providers;
    private readonly ILogger<DeviceHistoryRepullService> _logger;

    public DeviceHistoryRepullService(
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access,
        IOptions<List<DeviceProviderSettings>> providers,
        ILogger<DeviceHistoryRepullService> logger)
    {
        _unitOfWork = unitOfWork;
        _access = access;
        _providers = providers.Value;
        _logger = logger;
    }

    public async Task<DeviceHistoryRepullResponse> RequestAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, int days, CancellationToken ct = default)
    {
        // View access, like the manual sync: a re-pull shows the caller nothing they could not
        // already see, and a relative watching over someone should be able to fill a gap they
        // noticed. The cooldown, not the tier, is what bounds the quota spend.
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            throw new KeyNotFoundException("CardiMember not found");

        var now = DateTime.UtcNow;

        // Same gate as every other collection path: a paused member's data must not be
        // collected, and a re-pull is collection.
        if (member.IsMonitoringPaused(now))
        {
            throw new HistoryRepullUnavailableException(
                HistoryRepullUnavailableException.MonitoringPaused,
                "Monitoring is paused, so we're not collecting data right now.");
        }

        var connections = (await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(cardiMemberId)).ToList();
        var connection = connections.FirstOrDefault(c => c.Id == deviceId)
            ?? throw new KeyNotFoundException("Device not found");

        var config = _providers.ConfigFor(connection.DeviceType);
        if (!IsSyncable(connection) || config is null)
        {
            throw new HistoryRepullUnavailableException(
                HistoryRepullUnavailableException.DeviceNotSyncable,
                "This device isn't connected right now — refresh or reconnect it first.");
        }

        if (await _unitOfWork.DeviceHistoryRepulls.GetOpenByConnectionIdAsync(connection.Id, ct) is not null)
        {
            throw new HistoryRepullUnavailableException(
                HistoryRepullUnavailableException.RepullInProgress,
                "We're already re-pulling this device's history — check back in a little while.");
        }

        var cooldown = TimeSpan.FromHours(config.HistoryRepullCooldownHours);
        if (cooldown > TimeSpan.Zero
            && await _unitOfWork.DeviceHistoryRepulls.GetLastCompletedAtAsync(connection.Id, ct) is { } lastCompleted
            && lastCompleted + cooldown > now)
        {
            throw new HistoryRepullUnavailableException(
                HistoryRepullUnavailableException.TooSoon,
                $"This device's history was re-pulled recently — you can do it again {Describe(lastCompleted + cooldown - now)}.");
        }

        // Defence in depth behind the request validator: the range arithmetic refuses anything
        // outside 1..MaxDays too, but a clear message beats an ArgumentOutOfRangeException.
        if (days < 1 || days > HistoryRepullWindow.MaxDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(days), days, $"Days must be between 1 and {HistoryRepullWindow.MaxDays}.");
        }

        var (from, to) = HistoryRepullWindow.Bounds(DateOnly.FromDateTime(now), days);
        var repull = new DeviceHistoryRepull
        {
            DeviceConnectionId = connection.Id,
            CardiMemberId = cardiMemberId,
            RequestedByUserId = requestingUserId,
            FromDate = from,
            ToDate = to,
            Status = HistoryRepullStatus.Pending,
            RequestedAt = now,
        };

        try
        {
            await _unitOfWork.DeviceHistoryRepulls.AddAsync(repull);
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // The get-then-insert above raced another request for the same connection and the
            // partial unique index decided it. The other request is the one running; this one
            // reads exactly as if the check had caught it.
            throw new HistoryRepullUnavailableException(
                HistoryRepullUnavailableException.RepullInProgress,
                "We're already re-pulling this device's history — check back in a little while.");
        }

        _logger.LogInformation(
            "History re-pull {RepullId} queued for DeviceConnection {DeviceConnectionId} " +
            "(CardiMember {CardiMemberId}): {From} to {To}, requested by user {UserId}.",
            repull.Id, connection.Id, cardiMemberId, from, to, requestingUserId);

        return HistoryRepullWindow.ToResponse(repull, cooldown, now);
    }

    /// <summary>
    /// The statuses the routine sync pulls: Connected, and SyncError — a connection the provider
    /// hiccuped on last time is still one worth asking again. TokenExpired and AuthError are
    /// not: there is no usable grant to re-pull with until the caregiver reconnects.
    /// </summary>
    private static bool IsSyncable(DeviceConnection connection) =>
        connection.IsActive
        && connection.ConnectionStatus is ConnectionStatus.Connected or ConnectionStatus.SyncError;

    private static string Describe(TimeSpan wait)
    {
        // Hours up to two days — the same wording HistoryRepullCopy uses on the card, so the
        // refusal and the status line under the action agree.
        var hours = (int)Math.Ceiling(wait.TotalHours);
        return hours switch
        {
            <= 1 => "in about an hour",
            < 48 => $"in about {hours} hours",
            _ => $"in about {(int)Math.Ceiling(wait.TotalDays)} days",
        };
    }
}
