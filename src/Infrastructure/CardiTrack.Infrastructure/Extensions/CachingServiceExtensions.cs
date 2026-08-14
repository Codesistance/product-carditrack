using CardiTrack.Infrastructure.Security;
using CardiTrack.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace CardiTrack.Infrastructure.Extensions;

public static class CachingServiceExtensions
{
    /// <summary>
    /// Registers <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> —
    /// Redis when <c>ConnectionStrings:Redis</c> is configured, an in-process cache otherwise.
    /// Shared by every host that can resolve a service depending on the distributed cache
    /// (API, Worker, PipelineJobs): a host that skips this call passes DI validation right up
    /// until something tries to construct that service, then fails at the first request or run.
    /// </summary>
    public static IServiceCollection AddCachingServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var configLoader = new ConfigurationLoader(configuration);
        var redisConnection = configLoader.Get(ConfigurationKeys.ConnectionStrings.Redis);

        // Whitespace counts as unset, matching ConfigurationLoader.GetRequired: an env var set
        // to blanks otherwise reaches ConfigurationOptions.Parse and throws instead of falling
        // back to the in-memory cache.
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            var redisCaCertificate = configLoader.Get(ConfigurationKeys.Redis.CaCertificate);

            services.AddStackExchangeRedisCache(options =>
            {
                var redisOptions = ConfigurationOptions.Parse(redisConnection);

                // Only Memorystore ships a CA; locally docker-compose speaks plain Redis and
                // leaves this unset, so the defaults parsed from the connection string stand.
                if (!string.IsNullOrWhiteSpace(redisCaCertificate))
                {
                    // The pinned CA is not a public issuer, so revocation cannot be checked.
                    redisOptions.CheckCertificateRevocation = false;
                    redisOptions.CertificateValidation +=
                        RedisCertificateValidation.Create(redisCaCertificate);
                }

                options.ConfigurationOptions = redisOptions;
                options.InstanceName = "CardiTrack_";
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        services.AddMemoryCache();

        return services;
    }
}
