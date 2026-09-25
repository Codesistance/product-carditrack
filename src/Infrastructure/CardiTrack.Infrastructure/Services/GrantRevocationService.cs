using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.Services;

/// <inheritdoc cref="IGrantRevocationService"/>
/// <remarks>
/// <para>
/// Each queued grant is re-checked immediately before the provider call. The decision to revoke
/// was taken when the device was removed or replaced, but a grant for the same account may have
/// been stored since, and revocation ends the grant for the whole account. A grant found shared is
/// dropped from the queue, not revoked.
/// </para>
/// <para>
/// A failure is retried on a widening backoff. After <see cref="MaxAttempts"/> the row is deleted
/// anyway, and an error is logged naming the connection: the token must not sit in the database
/// indefinitely, and by then the grant can only be ended from the wearer's provider account.
/// </para>
/// </remarks>
public class GrantRevocationService : IGrantRevocationService
{
    /// <summary>Attempts before giving up — about three days on the backoff below.</summary>
    public const int MaxAttempts = 8;

    private const int BatchSize = 50;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IOAuthGrantRevoker _revoker;
    private readonly List<DeviceProviderSettings> _providers;
    private readonly ILogger<GrantRevocationService> _logger;

    public GrantRevocationService(
        IUnitOfWork unitOfWork,
        IOAuthGrantRevoker revoker,
        IOptions<List<DeviceProviderSettings>> providers,
        ILogger<GrantRevocationService> logger)
    {
        _unitOfWork = unitOfWork;
        _revoker = revoker;
        _providers = providers.Value;
        _logger = logger;
    }

    public async Task<int> RevokeDueAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var due = await _unitOfWork.PendingGrantRevocations.GetDueAsync(utcNow, BatchSize, ct);

        var ended = 0;
        foreach (var pending in due)
        {
            ct.ThrowIfCancellationRequested();
            if (await ProcessAsync(pending, utcNow, ct))
                ended++;

            // One row at a time, so a crash part-way through a batch neither loses the rows already
            // settled nor revokes them twice.
            await _unitOfWork.SaveChangesAsync();
        }

        return ended;
    }

    private async Task<bool> ProcessAsync(PendingGrantRevocation pending, DateTime utcNow, CancellationToken ct)
    {
        var memberConnections = await _unitOfWork.DeviceConnections.GetByCardiMemberIdAsync(pending.CardiMemberId);
        if (await DeviceGrantSharing.MayBeSharedAsync(
                _unitOfWork.DeviceConnections, _providers, pending.DeviceConnectionId,
                pending.DeviceType, pending.HealthUserId, memberConnections))
        {
            _logger.LogInformation(
                "Kept the {DeviceType} grant of DeviceConnection {DeviceConnectionId}: a live "
                + "connection may read through the same account.",
                pending.DeviceType, pending.DeviceConnectionId);
            _unitOfWork.PendingGrantRevocations.Remove(pending);
            return false;
        }

        // A provider with no revocation endpoint configured will not grow one between attempts, so
        // there is nothing to retry — the revoker already logs that it had nowhere to send it.
        if (string.IsNullOrWhiteSpace(_providers.ConfigFor(pending.DeviceType)?.RevocationUrl))
        {
            await _revoker.TryRevokeAsync(AsConnection(pending), ct);
            _unitOfWork.PendingGrantRevocations.Remove(pending);
            return false;
        }

        bool revoked;
        try
        {
            revoked = await _revoker.TryRevokeAsync(AsConnection(pending), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // An HTTP timeout surfaces as a cancellation; the pass itself was not cancelled, so it
            // is a failed attempt like any other.
            _logger.LogWarning(ex,
                "Revoking the grant of DeviceConnection {DeviceConnectionId} failed.",
                pending.DeviceConnectionId);
            revoked = false;
        }

        if (revoked)
        {
            _unitOfWork.PendingGrantRevocations.Remove(pending);
            return true;
        }

        pending.Attempts++;
        if (pending.Attempts >= MaxAttempts)
        {
            _logger.LogError(
                "Gave up revoking the {DeviceType} grant of DeviceConnection {DeviceConnectionId} "
                + "after {Attempts} attempts. It may still be live at the provider; revoke it from "
                + "the wearer's provider account.",
                pending.DeviceType, pending.DeviceConnectionId, pending.Attempts);
            _unitOfWork.PendingGrantRevocations.Remove(pending);
            return false;
        }

        pending.NextAttemptAt = utcNow + BackoffAfter(pending.Attempts);
        pending.UpdatedDate = utcNow;
        _unitOfWork.PendingGrantRevocations.Update(pending);
        return false;
    }

    /// <summary>5 minutes, tripling, capped at a day: 5m, 15m, 45m, 2h15, 6h45, 20h15, 24h.</summary>
    public static TimeSpan BackoffAfter(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(24 * 60, 5 * Math.Pow(3, Math.Max(0, attempts - 1))));

    /// <summary>What the revoker reads: the brand, and the token as the refresh token it prefers.</summary>
    private static DeviceConnection AsConnection(PendingGrantRevocation pending) => new()
    {
        Id = pending.DeviceConnectionId,
        CardiMemberId = pending.CardiMemberId,
        DeviceType = pending.DeviceType,
        HealthUserId = pending.HealthUserId,
        RefreshToken = pending.Token,
    };
}
