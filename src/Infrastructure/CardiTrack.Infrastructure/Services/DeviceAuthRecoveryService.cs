using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The auth self-heal. Retries the refresh token of connections the provider has refused, on a
/// widening backoff, and puts the ones that answer back into service.
/// </summary>
/// <remarks>
/// <para>
/// A refused refresh is not proof of a revoked grant. Providers return <c>invalid_grant</c> for
/// clock skew and during their own incidents, and a wearer who re-grants access on Google's
/// consent screen changes nothing on our side. But a refused connection is dropped from
/// <c>GetDueForSyncAsync</c>'s statuses, so nothing else in the system will ever touch it again:
/// without this pass, a connection that was only briefly refused stays dead until a caregiver
/// notices the dashboard has been quiet and reconnects by hand. On a product whose promise is to
/// notice things on their behalf, "we stopped watching and waited for you to spot it" is the
/// worst available behaviour.
/// </para>
/// <para>
/// The backoff is what keeps this honest about the other case. A genuinely revoked grant fails
/// every time, so it settles at the daily interval — one request a day to the provider rather
/// than ninety-six — and the <c>DEVICE_AUTH_BROKEN</c> nudge keeps asking the caregiver to
/// reconnect, because re-consent is the only thing that will ever fix that one.
/// </para>
/// <para>
/// Recovery is deliberately silent. A connection that comes back on its own is not news, and the
/// gap resolver clears the standing nudge as part of the next evaluation — a caregiver who never
/// found out anything was wrong should not be told afterwards that it was.
/// </para>
/// </remarks>
public class DeviceAuthRecoveryService : IDeviceAuthRecoveryService
{
    /// <summary>
    /// How long to wait after the first, second, third failure and so on. The last entry is the
    /// resting interval every further attempt takes: quick enough that a provider incident is
    /// picked up within the hour, slow enough that a grant nobody is coming back for costs one
    /// request a day.
    /// </summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
    ];

    private readonly IUnitOfWork _unitOfWork;
    private readonly IOAuthTokenRefreshService _tokenRefresh;
    private readonly INotificationGapResolver _gapResolver;
    private readonly List<DeviceProviderSettings> _providers;
    private readonly ILogger<DeviceAuthRecoveryService> _logger;

    public DeviceAuthRecoveryService(
        IUnitOfWork unitOfWork,
        IOAuthTokenRefreshService tokenRefresh,
        INotificationGapResolver gapResolver,
        IOptions<List<DeviceProviderSettings>> providers,
        ILogger<DeviceAuthRecoveryService> logger)
    {
        _unitOfWork = unitOfWork;
        _tokenRefresh = tokenRefresh;
        _gapResolver = gapResolver;
        _providers = providers.Value;
        _logger = logger;
    }

    public async Task<int> RecoverDueConnectionsAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var due = (await _unitOfWork.DeviceConnections.GetDueForAuthRecoveryAsync(utcNow)).ToList();

        var recovered = 0;
        foreach (var connection in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await TryRecoverAsync(connection, utcNow, ct))
                    recovered++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One connection's failure must not cost the rest of the fleet this pass. The
                // attempt is still recorded, so a connection that throws every time backs off
                // like any other rather than being retried at full rate forever.
                _logger.LogError(
                    ex, "Auth recovery threw for DeviceConnection {DeviceConnectionId}.", connection.Id);
                await ScheduleNextAttemptAsync(connection, utcNow);
            }
        }

        _logger.LogInformation(
            "Device auth recovery complete. Due: {Due}, recovered: {Recovered}.", due.Count, recovered);
        return recovered;
    }

    private async Task<bool> TryRecoverAsync(DeviceConnection connection, DateTime utcNow, CancellationToken ct)
    {
        var providerConfig = _providers.ConfigFor(connection.DeviceType);
        if (providerConfig is null)
        {
            // No config claims this device type, so no amount of retrying will help — and it is a
            // deployment fault rather than a grant fault. Backed off like the rest so a
            // misconfigured provider does not spin.
            _logger.LogWarning(
                "No provider config claims device type {DeviceType} for DeviceConnection {DeviceConnectionId}; "
                + "auth recovery cannot run for it.",
                connection.DeviceType, connection.Id);
            await ScheduleNextAttemptAsync(connection, utcNow);
            return false;
        }

        try
        {
            // The same call the sync path makes. Reusing it is the point: recovery must prove the
            // exact refresh a real pull will perform, not a lookalike that could succeed where
            // the real one fails.
            await _tokenRefresh.RefreshIfExpiredAsync(connection, providerConfig);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                ex, "Auth recovery attempt {Attempt} failed for DeviceConnection {DeviceConnectionId}.",
                connection.AuthRecoveryAttempts + 1, connection.Id);
            await ScheduleNextAttemptAsync(connection, utcNow);
            return false;
        }

        await _unitOfWork.DeviceConnections.MarkAuthRecoveredAsync(connection.Id);

        // Clears the standing DEVICE_AUTH_BROKEN nudge now rather than at tomorrow's rule run —
        // a caregiver should not be told to reconnect a device that is already working again.
        await _gapResolver.ResolveForCardiMemberAsync(connection.CardiMemberId, ct);

        _logger.LogInformation(
            "DeviceConnection {DeviceConnectionId} recovered after {Attempts} failed attempt(s); "
            + "returned to the sync rotation.",
            connection.Id, connection.AuthRecoveryAttempts);
        return true;
    }

    private Task ScheduleNextAttemptAsync(DeviceConnection connection, DateTime utcNow) =>
        _unitOfWork.DeviceConnections.MarkAuthRecoveryFailedAsync(
            connection.Id, utcNow + DelayAfter(connection.AuthRecoveryAttempts));

    /// <summary>
    /// The wait after <paramref name="failuresSoFar"/> failures, resting at the last step.
    /// </summary>
    internal static TimeSpan DelayAfter(int failuresSoFar) =>
        Backoff[Math.Clamp(failuresSoFar, 0, Backoff.Length - 1)];
}
