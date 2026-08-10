namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Generic wearable API client. Each provider implements this interface.
/// </summary>
public interface IDeviceApiClient
{
    Task<DeviceHealthSnapshot> GetHealthSnapshotAsync(string accessToken, DateOnly date);

    /// <summary>
    /// The member's sub-daily series for one civil day — timestamped raw readings for the
    /// granular substrate, alongside (not instead of) the daily snapshot. A device that records
    /// none of the granular metrics returns <see cref="DeviceGranularDay.Empty"/>.
    /// </summary>
    Task<DeviceGranularDay> GetGranularDayAsync(string accessToken, DateOnly date);

    /// <summary>
    /// The wearer's public health-user id — the `users/{user}` segment webhook notifications and
    /// subscriptions are addressed by. Null when the provider does not expose one.
    /// </summary>
    Task<string?> GetHealthUserIdAsync(string accessToken);
}
