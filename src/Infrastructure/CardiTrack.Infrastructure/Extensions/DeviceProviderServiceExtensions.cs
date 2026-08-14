using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.Extensions;

public static class DeviceProviderServiceExtensions
{
    /// <summary>
    /// Registers the Google Health API engine: HTTP client, keyed IDeviceApiClient, and keyed
    /// IDeviceSyncService, all keyed by <see cref="HealthApi.GoogleHealth"/>. Which hardware
    /// brands (DeviceTypes) ride this engine is configuration — the provider block's DeviceTypes
    /// list — not code. Call services.AddGoogleHealthProvider() in both the API and Functions DI
    /// setup. To add a new API, create an equivalent AddGarminConnectProvider() /
    /// AddSamsungHealthProvider() etc.
    /// </summary>
    public static IServiceCollection AddGoogleHealthProvider(this IServiceCollection services)
    {
        // Deployment injects secrets positionally (DeviceProviders__0__ClientId etc. in
        // infrastructure/main.tf), so element 0 must be the GoogleHealth provider. Fail fast on a
        // reordered appsettings list instead of silently binding Google credentials to the
        // wrong provider.
        services.PostConfigure<List<DeviceProviderSettings>>(providers =>
        {
            if (providers.Count > 0 && !string.Equals(
                    providers[0].Provider, nameof(HealthApi.GoogleHealth), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"DeviceProviders[0] must be the GoogleHealth provider (found '{providers[0].Provider}') — " +
                    "deployment env vars bind its secrets by index (DeviceProviders__0__*).");
            }

            var claimedDeviceTypes = new Dictionary<DeviceType, string>();

            foreach (var provider in providers)
            {
                // The Provider name is the engine key: a typo here would leave every connection
                // of the block's device types unroutable, discovered only when a sync silently
                // skips them — so it stops the host instead.
                if (provider.Api() is null)
                {
                    throw new InvalidOperationException(
                        $"DeviceProviders '{provider.Provider}': Provider must name a HealthApi " +
                        $"({string.Join(", ", Enum.GetNames<HealthApi>())}).");
                }

                // An API with no device types can never be reached, and an unknown device type
                // name is a mapping that will never match a connection — both are dead config
                // that reads as support for something the runtime cannot route.
                if (provider.DeviceTypes.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"DeviceProviders '{provider.Provider}': DeviceTypes must list at least " +
                        "one DeviceType this API serves (e.g. [\"Fitbit\", \"GooglePixelWatch\"]).");
                }

                foreach (var name in provider.DeviceTypes)
                {
                    if (!Enum.TryParse<DeviceType>(name, ignoreCase: true, out var deviceType))
                    {
                        throw new InvalidOperationException(
                            $"DeviceProviders '{provider.Provider}': DeviceTypes entry '{name}' " +
                            $"is not a DeviceType ({string.Join(", ", Enum.GetNames<DeviceType>())}).");
                    }

                    // First-match resolution would make a double claim an ordering accident —
                    // whichever block binds first silently owns the brand's OAuth and sync.
                    if (!claimedDeviceTypes.TryAdd(deviceType, provider.Provider))
                    {
                        throw new InvalidOperationException(
                            $"DeviceProviders: DeviceType '{deviceType}' is claimed by both " +
                            $"'{claimedDeviceTypes[deviceType]}' and '{provider.Provider}' — a device " +
                            "type may be served by exactly one API.");
                    }
                }

                // Cadence bounds are the only guard on calibrated pull intervals, so a malformed
                // pair must stop the host rather than silently clamp to nonsense — an inverted
                // range would otherwise pin every connection of that type to one end of it.
                if (provider.MinPullIntervalMinutes <= 0 || provider.MaxPullIntervalMinutes <= 0)
                {
                    throw new InvalidOperationException(
                        $"DeviceProviders '{provider.Provider}': MinPullIntervalMinutes and " +
                        "MaxPullIntervalMinutes must both be greater than zero (found " +
                        $"{provider.MinPullIntervalMinutes} and {provider.MaxPullIntervalMinutes}).");
                }

                if (provider.MinPullIntervalMinutes > provider.MaxPullIntervalMinutes)
                {
                    throw new InvalidOperationException(
                        $"DeviceProviders '{provider.Provider}': MinPullIntervalMinutes " +
                        $"({provider.MinPullIntervalMinutes}) exceeds MaxPullIntervalMinutes " +
                        $"({provider.MaxPullIntervalMinutes}).");
                }

                if (provider.DormancyThresholdPulls > 0 && provider.DormancyBackoffFactor <= 1)
                {
                    throw new InvalidOperationException(
                        $"DeviceProviders '{provider.Provider}': DormancyBackoffFactor must be " +
                        $"greater than 1 when backoff is enabled (found {provider.DormancyBackoffFactor}) — " +
                        "a factor of 1 or less never widens the interval.");
                }
            }
        });

        services.AddHttpClient("GoogleHealthClient")
            .ConfigureHttpClient((sp, client) =>
            {
                var config = sp.GetRequiredService<IOptions<List<DeviceProviderSettings>>>().Value
                    .FirstOrDefault(p => string.Equals(
                        p.Provider, nameof(HealthApi.GoogleHealth), StringComparison.OrdinalIgnoreCase));
                client.BaseAddress = new Uri(string.IsNullOrEmpty(config?.ApiBaseUrl)
                    ? "https://health.googleapis.com"
                    : config.ApiBaseUrl);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });

        services.AddKeyedScoped<IDeviceApiClient, GoogleHealthApiClient>(HealthApi.GoogleHealth);

        // Provider-neutral, but registered here because only the sync services constructed below
        // consume it.
        services.AddScoped<IGranularIngestionService, GranularIngestionService>();

        services.AddKeyedScoped<IDeviceSyncService>(
            HealthApi.GoogleHealth,
            (sp, _) => new DeviceSyncService(
                sp.GetRequiredService<IOAuthTokenRefreshService>(),
                sp.GetRequiredKeyedService<IDeviceApiClient>(HealthApi.GoogleHealth),
                sp.GetRequiredService<IDeviceConnectionRepository>(),
                sp.GetRequiredService<IDeviceActivityLogRepository>(),
                sp.GetRequiredService<IActivityLogAggregationService>(),
                sp.GetRequiredService<IGranularIngestionService>(),
                sp.GetRequiredService<IUnitOfWork>(),
                sp.GetRequiredService<INotificationGapResolver>(),
                sp.GetRequiredService<IOptions<List<DeviceProviderSettings>>>()));

        return services;
    }
}
