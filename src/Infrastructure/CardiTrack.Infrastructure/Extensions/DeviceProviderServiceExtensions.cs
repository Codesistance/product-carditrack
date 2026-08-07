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
    /// Registers the Fitbit device provider: HTTP client, keyed IDeviceApiClient, and keyed IDeviceSyncService.
    /// Call services.AddFitbitProvider() in both the API and Functions DI setup.
    /// To add a new provider, create an equivalent AddGarminProvider() / AddAppleWatchProvider() etc.
    /// </summary>
    public static IServiceCollection AddFitbitProvider(this IServiceCollection services)
    {
        // Deployment injects secrets positionally (DeviceProviders__0__ClientId etc. in
        // infrastructure/main.tf), so element 0 must be the Fitbit provider. Fail fast on a
        // reordered appsettings list instead of silently binding Google credentials to the
        // wrong provider.
        services.PostConfigure<List<DeviceProviderSettings>>(providers =>
        {
            if (providers.Count > 0 && !string.Equals(
                    providers[0].Provider, nameof(DeviceType.Fitbit), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"DeviceProviders[0] must be the Fitbit provider (found '{providers[0].Provider}') — " +
                    "deployment env vars bind its secrets by index (DeviceProviders__0__*).");
            }
        });

        services.AddHttpClient("FitbitClient")
            .ConfigureHttpClient((sp, client) =>
            {
                var config = sp.GetRequiredService<IOptions<List<DeviceProviderSettings>>>().Value
                    .FirstOrDefault(p => string.Equals(
                        p.Provider, nameof(DeviceType.Fitbit), StringComparison.OrdinalIgnoreCase));
                client.BaseAddress = new Uri(string.IsNullOrEmpty(config?.ApiBaseUrl)
                    ? "https://health.googleapis.com"
                    : config.ApiBaseUrl);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });

        services.AddKeyedScoped<IDeviceApiClient, FitbitApiClient>(DeviceType.Fitbit);

        services.AddKeyedScoped<IDeviceSyncService>(
            DeviceType.Fitbit,
            (sp, _) => new DeviceSyncService(
                sp.GetRequiredService<IOAuthTokenRefreshService>(),
                sp.GetRequiredKeyedService<IDeviceApiClient>(DeviceType.Fitbit),
                sp.GetRequiredService<IDeviceConnectionRepository>(),
                sp.GetRequiredService<IDeviceActivityLogRepository>(),
                sp.GetRequiredService<IActivityLogAggregationService>(),
                sp.GetRequiredService<IUnitOfWork>(),
                sp.GetRequiredService<IOptions<List<DeviceProviderSettings>>>()));

        return services;
    }
}
