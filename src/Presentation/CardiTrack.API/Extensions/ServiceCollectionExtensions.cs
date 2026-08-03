using AspNetCoreRateLimit;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared;
using FluentValidation;

namespace CardiTrack.API.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddValidators(this IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateOrganizationRequest>, CreateOrganizationValidator>();
        services.AddScoped<IValidator<CreateCardiMemberRequest>, CreateCardiMemberValidator>();
        services.AddScoped<IValidator<NotificationPreferencesRequest>, NotificationPreferencesValidator>();
        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IOrganizationService, CardiTrack.Application.Services.OrganizationService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IUserService, CardiTrack.Application.Services.UserService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.ICardiMemberService, CardiTrack.Application.Services.CardiMemberService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.ISubscriptionService, CardiTrack.Application.Services.SubscriptionService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IDashboardService, CardiTrack.Application.Services.DashboardService>();

        return services;
    }

    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Device provider config array
        services.Configure<List<DeviceProviderSettings>>(
            configuration.GetSection(DeviceProviderSettings.SectionName));

        // Encryption — key must be a base64-encoded 256-bit value stored in config/Key Vault
        services.AddSingleton<ConfigurationLoader>();
        services.AddScoped<IEncryptionService>(sp =>
        {
            var loader = sp.GetRequiredService<ConfigurationLoader>();
            var key = loader.GetRequired(ConfigurationKeys.Encryption.Key);
            return new AesEncryptionService(key);
        });

        // Repositories
        services.AddScoped<IOrganizationRepository, CardiTrack.Infrastructure.Repositories.OrganizationRepository>();
        services.AddScoped<IUserRepository, CardiTrack.Infrastructure.Repositories.UserRepository>();
        services.AddScoped<ICardiMemberRepository, CardiTrack.Infrastructure.Repositories.CardiMemberRepository>();
        services.AddScoped<ISubscriptionRepository, CardiTrack.Infrastructure.Repositories.SubscriptionRepository>();
        services.AddScoped<IUserCardiMemberRepository, CardiTrack.Infrastructure.Repositories.UserCardiMemberRepository>();
        services.AddScoped<IDeviceConnectionRepository, CardiTrack.Infrastructure.Repositories.DeviceConnectionRepository>();
        services.AddScoped<IActivityLogRepository, CardiTrack.Infrastructure.Repositories.ActivityLogRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<IAlertRepository, CardiTrack.Infrastructure.Repositories.AlertRepository>();
        services.AddScoped<IPatternBaselineRepository, CardiTrack.Infrastructure.Repositories.PatternBaselineRepository>();

        // Unit of Work
        services.AddScoped<IUnitOfWork, CardiTrack.Infrastructure.Repositories.UnitOfWork>();

        // AI services
        var configLoader = new ConfigurationLoader(configuration);
        services.AddAiServices(configuration, configLoader);

        // External clients
        services.AddScoped<IOAuthTokenRefreshService, OAuthTokenRefreshService>();

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
        var redisConnection = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrEmpty(redisConnection))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnection;
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
        services.Configure<IpRateLimitOptions>(configuration.GetSection("IpRateLimiting"));
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
