using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.ExternalClients.Storage;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.Infrastructure.Extensions;

/// <summary>
/// Wires member profile photo storage (processor + GCS adapter). Registered unconditionally by
/// the API — unlike <see cref="EnvironmentalServiceExtensions"/> there is no fail-at-startup
/// check, because an absent bucket is a supported state, not a misconfiguration: every local
/// machine runs without one. In that state reads resolve to null (initials avatars) and photo
/// upload/removal throw a clear <see cref="InvalidOperationException"/>.
/// </summary>
public static class StorageServiceExtensions
{
    public static IServiceCollection AddMemberPhotoStorage(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ConfigurationKeys.Storage.MemberPhotosSectionName)
            .Get<MemberPhotoStorageOptions>() ?? new MemberPhotoStorageOptions();

        services.AddSingleton(options);
        // Backs the signed-URL cache. Idempotent — the API also registers it for rate limiting.
        services.AddMemoryCache();

        // Singletons: the processor is stateless, and the storage adapter's GCS client, URL
        // signer and log-once flag are all meant to live for the process.
        services.AddSingleton<IProfilePhotoProcessor, ImageSharpProfilePhotoProcessor>();
        services.AddSingleton<IProfilePhotoStorage, GcsProfilePhotoStorage>();

        return services;
    }
}
