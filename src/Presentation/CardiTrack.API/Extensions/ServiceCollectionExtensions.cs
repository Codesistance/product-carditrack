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

namespace CardiTrack.API.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddValidators(this IServiceCollection services)
    {
        services.AddScoped<IValidator<CreateOrganizationRequest>, CreateOrganizationValidator>();
        services.AddScoped<IValidator<CreateCardiMemberRequest>, CreateCardiMemberValidator>();
        services.AddScoped<IValidator<UpdateCardiMemberRequest>, UpdateCardiMemberValidator>();
        services.AddScoped<IValidator<PauseMonitoringRequest>, PauseMonitoringValidator>();
        services.AddScoped<IValidator<ConnectDeviceRequest>, ConnectDeviceValidator>();
        services.AddScoped<IValidator<OAuthCallbackRequest>, OAuthCallbackValidator>();
        services.AddScoped<IValidator<AnswerQuestionnaireRequest>, AnswerQuestionnaireValidator>();
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
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IDigestQueryService, CardiTrack.Application.Services.DigestQueryService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IQuestionnaireService, CardiTrack.Application.Services.QuestionnaireService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IAlertService, CardiTrack.Application.Services.AlertService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IActivityLogAggregationService, CardiTrack.Application.Services.ActivityLogAggregationService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.IOnboardingService, CardiTrack.Application.Services.OnboardingService>();
        services.AddScoped<CardiTrack.Application.Interfaces.Services.INotificationService, CardiTrack.Application.Services.Notifications.NotificationService>();

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
        services.AddScoped<IDigestRepository, CardiTrack.Infrastructure.Repositories.DigestRepository>();
        services.AddScoped<IRealtimeAssessmentRepository, CardiTrack.Infrastructure.Repositories.RealtimeAssessmentRepository>();
        services.AddScoped<IMemberQuestionnaireRepository, CardiTrack.Infrastructure.Repositories.MemberQuestionnaireRepository>();
        services.AddScoped<IEnvironmentalReadingRepository, CardiTrack.Infrastructure.Repositories.EnvironmentalReadingRepository>();
        services.AddScoped<IAuditLogRepository, CardiTrack.Infrastructure.Repositories.AuditLogRepository>();
        services.AddScoped<INotificationRepository, CardiTrack.Infrastructure.Repositories.NotificationRepository>();
        services.AddScoped<INotificationMuteRepository, CardiTrack.Infrastructure.Repositories.NotificationMuteRepository>();
        services.AddScoped<INotificationSnapshotQueries, CardiTrack.Infrastructure.Repositories.NotificationSnapshotQueries>();

        // Push delivery spine (notification_engine.md Phase 3) — the API both issues the
        // immediate-attempt send (nudge/alert writing paths, and the internal enqueue endpoint)
        // and validates ack/fetch tokens, so it gets the full stack, not just the repositories.
        services.AddPushServices(configuration);

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
        services.AddGoogleHealthProvider();

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
