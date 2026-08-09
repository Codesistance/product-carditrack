using AspNetCoreRateLimit;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared;
using FluentValidation;
using StackExchange.Redis;

namespace CardiTrack.API.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddValidators(this IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateOrganizationRequest>, CreateOrganizationValidator>();
        services.AddScoped<IValidator<CreateCardiMemberRequest>, CreateCardiMemberValidator>();
        services.AddScoped<IValidator<UpdateCardiMemberRequest>, UpdateCardiMemberValidator>();
        services.AddScoped<IValidator<PauseMonitoringRequest>, PauseMonitoringValidator>();
        services.AddScoped<IValidator<NotificationPreferencesRequest>, NotificationPreferencesValidator>();
        services.AddScoped<IValidator<ConnectDeviceRequest>, ConnectDeviceValidator>();
        services.AddScoped<IValidator<OAuthCallbackRequest>, OAuthCallbackValidator>();
        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<CardiTrack.Application.Interfaces.Services.ICardiMemberAccessService, CardiTrack.Application.Services.CardiMemberAccessService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IOrganizationService, CardiTrack.Application.Services.OrganizationService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IUserService, CardiTrack.Application.Services.UserService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.ICardiMemberService, CardiTrack.Application.Services.CardiMemberService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.ISubscriptionService, CardiTrack.Application.Services.SubscriptionService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IDashboardService, CardiTrack.Application.Services.DashboardService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IAlertService, CardiTrack.Application.Services.AlertService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IActivityLogAggregationService, CardiTrack.Application.Services.ActivityLogAggregationService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IOnboardingService, CardiTrack.Application.Services.OnboardingService>();

        return services;
    }

    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Device provider config array
        services.Configure<List<DeviceProviderSettings>>(
            configuration.GetSection(DeviceProviderSettings.SectionName));

        var configLoader = new ConfigurationLoader(configuration);

        // Encryption — key must be a base64-encoded 256-bit value stored in config/Secret Manager.
        // Built here rather than in a factory so a missing or malformed key fails the host at
        // startup instead of surfacing as a 500 on the first request that touches a device endpoint.
        // The service holds only the key (AesGcm instances are per-call), so it is safe as a singleton.
        services.AddSingleton(configLoader);
        services.AddSingleton<IEncryptionService>(
            new AesEncryptionService(configLoader.GetRequired(ConfigurationKeys.Encryption.Key)));

        // Repositories
        services.AddScoped<IOrganizationRepository, CardiTrack.Infrastructure.Repositories.OrganizationRepository>();
        services.AddScoped<IUserRepository, CardiTrack.Infrastructure.Repositories.UserRepository>();
        services.AddScoped<ICardiMemberRepository, CardiTrack.Infrastructure.Repositories.CardiMemberRepository>();
        services.AddScoped<ISubscriptionRepository, CardiTrack.Infrastructure.Repositories.SubscriptionRepository>();
        services.AddScoped<IUserCardiMemberRepository, CardiTrack.Infrastructure.Repositories.UserCardiMemberRepository>();
        services.AddScoped<IDeviceConnectionRepository, CardiTrack.Infrastructure.Repositories.DeviceConnectionRepository>();
        services.AddScoped<IActivityLogRepository, CardiTrack.Infrastructure.Repositories.ActivityLogRepository>();
        services.AddScoped<IDeviceActivityLogRepository, CardiTrack.Infrastructure.Repositories.DeviceActivityLogRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<IAlertRepository, CardiTrack.Infrastructure.Repositories.AlertRepository>();
        services.AddScoped<IPatternBaselineRepository, CardiTrack.Infrastructure.Repositories.PatternBaselineRepository>();
        services.AddScoped<IGranularMetricRepository, CardiTrack.Infrastructure.Repositories.GranularMetricRepository>();
        services.AddScoped<IAuditLogRepository, CardiTrack.Infrastructure.Repositories.AuditLogRepository>();

        // Unit of Work
        services.AddScoped<IUnitOfWork, CardiTrack.Infrastructure.Repositories.UnitOfWork>();

        // AI services
        services.AddAiServices(configuration);

        // External clients
        services.AddScoped<IOAuthTokenRefreshService, OAuthTokenRefreshService>();
        services.AddScoped<IOAuthCodeExchangeService, OAuthCodeExchangeService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IAuth0ManagementService, Auth0ManagementClient>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IDeviceConnectionService,
            CardiTrack.Infrastructure.Services.DeviceConnectionService>();
        // Caregiver-triggered sync (issue #67). Request-scoped, not a background job — the
        // scheduled pull stays CardiTrack.Worker's, per CLAUDE.md.
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IManualDeviceSyncService,
            CardiTrack.Infrastructure.Services.ManualDeviceSyncService>();

        // HTTP Client for Auth0 service
        services.AddHttpClient("Auth0Client", client =>
        {
            var auth0Domain = new ConfigurationLoader(configuration).GetRequired(ConfigurationKeys.Auth0.Domain);
            client.BaseAddress = new Uri($"https://{auth0Domain}/");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        // Fitbit provider (keyed IDeviceApiClient + keyed IDeviceSyncService)
        services.AddFitbitProvider();

        return services;
    }

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

    public static IServiceCollection AddRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMemoryCache();
        services.Configure<IpRateLimitOptions>(configuration.GetSection(ConfigurationKeys.IpRateLimiting.SectionName));
        services.AddInMemoryRateLimiting();
        services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();

        return services;
    }

    public static IServiceCollection AddUserContextServices(
        this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, UserContext>();

        return services;
    }
}
