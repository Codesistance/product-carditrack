using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Extensions;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Which provider account a freshly granted access token belongs to.
/// </summary>
/// <remarks>
/// This is what tells "a second device" from "the same device again" when a grant comes back.
/// The brand cannot — two Fitbits are two devices — and Google's token response carries no user
/// id, so the answer comes from the provider's own identity resource: the health-user id that
/// webhook notifications are addressed by, which is also what <see cref="Domain.Entities.DeviceConnection.HealthUserId"/>
/// stores.
/// </remarks>
public interface IDeviceAccountIdentityResolver
{
    /// <summary>
    /// The account's health-user id, or null when the provider exposes none or cannot be reached.
    /// Best-effort by design: a connection is still stored without one, it just cannot be matched
    /// against the member's other connections.
    /// </summary>
    Task<string?> TryResolveAsync(DeviceType deviceType, string accessToken, CancellationToken ct = default);
}

/// <inheritdoc />
public class DeviceAccountIdentityResolver : IDeviceAccountIdentityResolver
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DeviceAccountIdentityResolver> _logger;

    public DeviceAccountIdentityResolver(IServiceProvider services, ILogger<DeviceAccountIdentityResolver> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task<string?> TryResolveAsync(
        DeviceType deviceType, string accessToken, CancellationToken ct = default)
    {
        if (_services.GetDeviceApiClient(deviceType) is not { } client)
            return null;

        try
        {
            return await client.GetHealthUserIdAsync(accessToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Warning, not Error: the grant itself succeeded and is stored. What is lost is only the
            // ability to recognise it as an account the member already has, and the first sync
            // captures the id anyway.
            _logger.LogWarning(ex,
                "Could not read the provider account for a new {DeviceType} grant.", deviceType);
            return null;
        }
    }
}
