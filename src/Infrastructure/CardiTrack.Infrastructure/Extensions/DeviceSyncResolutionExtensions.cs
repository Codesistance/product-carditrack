using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.Extensions;

public static class DeviceSyncResolutionExtensions
{
    /// <summary>
    /// The sync engine serving <paramref name="deviceType"/>: the configured DeviceType→HealthApi
    /// mapping picks the API, and the API keys the registration. Null when no provider block
    /// claims the device type or no engine is registered for its API — callers treat that as
    /// "unsupported" the same way they treated a missing keyed service before.
    /// </summary>
    public static IDeviceSyncService? GetDeviceSyncService(
        this IServiceProvider services, DeviceType deviceType)
    {
        var configs = services.GetRequiredService<IOptions<List<DeviceProviderSettings>>>().Value;
        return configs.ApiFor(deviceType) is { } api
            ? services.GetKeyedService<IDeviceSyncService>(api)
            : null;
    }
}
