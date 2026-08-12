using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.UnitTests.Infrastructure;

public class TestDatabaseFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        _container = PostgreSqlTestContainerFactory.CreateStandardContainer();
        await _container.StartAsync();

        var services = new ServiceCollection();

        services.AddDbContext<CardiTrackDbContext>(options =>
            options.UseNpgsql(_container.GetConnectionString())
                   .EnableDetailedErrors()
                   .EnableSensitiveDataLogging());

        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<ICardiMemberRepository, CardiMemberRepository>();
        services.AddScoped<IDeviceConnectionRepository, DeviceConnectionRepository>();
        services.AddScoped<IActivityLogRepository, ActivityLogRepository>();
        services.AddScoped<IDeviceActivityLogRepository, DeviceActivityLogRepository>();
        services.AddScoped<IActivityLogAggregationService, ActivityLogAggregationService>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
        services.AddScoped<IUserCardiMemberRepository, UserCardiMemberRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<IAlertRepository, AlertRepository>();
        services.AddScoped<IPatternBaselineRepository, PatternBaselineRepository>();
        services.AddScoped<IGranularMetricRepository, GranularMetricRepository>();
        services.AddScoped<IDigestRepository, DigestRepository>();
        services.AddScoped<IRealtimeAssessmentRepository, RealtimeAssessmentRepository>();
        services.AddScoped<IEnvironmentalReadingRepository, EnvironmentalReadingRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationMuteRepository, NotificationMuteRepository>();
        services.AddScoped<INotificationDeliveryRepository, NotificationDeliveryRepository>();
        services.AddScoped<IPushDeviceTokenRepository, PushDeviceTokenRepository>();
        services.AddScoped<INotificationPreferenceRepository, NotificationPreferenceRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ITimeSeriesPartitionService, TimeSeriesPartitionService>();
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        await context.Database.MigrateAsync();
    }

    /// <summary>Creates a new DI scope. Each test should use its own scope.</summary>
    public IServiceScope CreateScope() => _serviceProvider.CreateScope();

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _container.DisposeAsync();
    }
}
