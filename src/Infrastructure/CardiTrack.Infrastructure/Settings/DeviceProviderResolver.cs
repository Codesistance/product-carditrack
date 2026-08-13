using CardiTrack.Domain.Enums;

namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// The one place the configured DeviceType→<see cref="HealthApi"/> mapping is read. Everything
/// that needs "which config/engine serves this device type" goes through here, so the mapping
/// stays configuration (<see cref="DeviceProviderSettings.DeviceTypes"/>) with a single
/// interpretation of it.
/// </summary>
public static class DeviceProviderResolver
{
    /// <summary>
    /// The provider block whose <see cref="DeviceProviderSettings.DeviceTypes"/> claims
    /// <paramref name="deviceType"/>, or null when no configured API serves that brand.
    /// </summary>
    public static DeviceProviderSettings? ConfigFor(
        this IEnumerable<DeviceProviderSettings> providers, DeviceType deviceType) =>
        providers.FirstOrDefault(p => p.DeviceTypes.Any(t =>
            string.Equals(t, deviceType.ToString(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The <see cref="HealthApi"/> a provider block configures — the key its engine is
    /// registered under. Null when <see cref="DeviceProviderSettings.Provider"/> is not a known
    /// API name; startup validation rejects that for hosts that register engines, so a null here
    /// means a host running with an unvalidated config.
    /// </summary>
    public static HealthApi? Api(this DeviceProviderSettings provider) =>
        Enum.TryParse<HealthApi>(provider.Provider, ignoreCase: true, out var api) ? api : null;

    /// <summary>The API serving <paramref name="deviceType"/> per configuration, or null.</summary>
    public static HealthApi? ApiFor(
        this IEnumerable<DeviceProviderSettings> providers, DeviceType deviceType) =>
        providers.ConfigFor(deviceType)?.Api();
}
