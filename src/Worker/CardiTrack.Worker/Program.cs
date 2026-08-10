using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Observability;
using CardiTrack.Shared;
using CardiTrack.Worker;
using CardiTrack.Worker.Workers;
using Microsoft.EntityFrameworkCore;
using Serilog;

// Enforce UTC for all DateTime values read from PostgreSQL timestamptz columns
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var configLoader = new ConfigurationLoader(configuration);

// LOGGING — same Serilog shape as CardiTrack.API: console always, plus APM
// shipping when the Apm engine is configured
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithProperty("Application", "CardiTrack.Worker")
    .Enrich.WithProperty("Version", DeploymentInfo.Version)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .AddApmShipping(configuration.GetApmOptions(), ApmServiceNames.Worker)
    .CreateLogger();

builder.Host.UseSerilog();

// APM TRACING — no-op until Apm__Engine + Apm__Data are configured
builder.AddApmTracing(ApmServiceNames.Worker);

// Device provider config array
builder.Services.Configure<List<DeviceProviderSettings>>(
    configuration.GetSection(DeviceProviderSettings.SectionName));

// Database
builder.Services.AddDbContext<CardiTrackDbContext>(options =>
    options.UseCardiTrackNpgsql(configLoader.Get(ConfigurationKeys.ConnectionStrings.DefaultConnection)));

// Encryption — key must be a base64-encoded 256-bit value in config/Secret Manager.
// Built eagerly so a missing or malformed key stops the Worker at startup rather than
// failing every token-refresh run. Safe as a singleton: it holds only the key.
builder.Services.AddSingleton(configLoader);
builder.Services.AddSingleton<IEncryptionService>(
    new AesEncryptionService(configLoader.GetRequired(ConfigurationKeys.Encryption.Key)));

// Repositories
builder.Services.AddScoped<IOrganizationRepository, OrganizationRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ICardiMemberRepository, CardiMemberRepository>();
builder.Services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
builder.Services.AddScoped<IUserCardiMemberRepository, UserCardiMemberRepository>();
builder.Services.AddScoped<IDeviceConnectionRepository, DeviceConnectionRepository>();
builder.Services.AddScoped<IActivityLogRepository, ActivityLogRepository>();
builder.Services.AddScoped<IDeviceActivityLogRepository, DeviceActivityLogRepository>();
builder.Services.AddScoped<IDeviceRepository, DeviceRepository>();
builder.Services.AddScoped<IAlertRepository, AlertRepository>();
builder.Services.AddScoped<IPatternBaselineRepository, PatternBaselineRepository>();
builder.Services.AddScoped<IGranularMetricRepository, GranularMetricRepository>();
builder.Services.AddScoped<IDigestRepository, DigestRepository>();
builder.Services.AddScoped<IRealtimeAssessmentRepository, RealtimeAssessmentRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<ITimeSeriesPartitionService, TimeSeriesPartitionService>();

// Application services
builder.Services.AddScoped<IActivityLogAggregationService, ActivityLogAggregationService>();
builder.Services.AddScoped<IInactivityDetectionService, InactivityDetectionService>();

// External clients
builder.Services.AddScoped<IOAuthTokenRefreshService, OAuthTokenRefreshService>();

// Fitbit provider (keyed IDeviceApiClient + keyed IDeviceSyncService)
builder.Services.AddFitbitProvider();

// Background workers
builder.Services.AddWorker<WearableSyncWorker>(configuration, nameof(WearableSyncWorker));
builder.Services.AddWorker<OrphanedOrganizationCleanupWorker>(configuration, nameof(OrphanedOrganizationCleanupWorker));
builder.Services.AddWorker<BaselineCalculationWorker>(configuration, nameof(BaselineCalculationWorker));
builder.Services.AddWorker<DeviceSyncAuditWorker>(configuration, nameof(DeviceSyncAuditWorker));
builder.Services.AddWorker<PartitionMaintenanceWorker>(configuration, nameof(PartitionMaintenanceWorker));
builder.Services.AddWorker<InactivityDetectionWorker>(configuration, nameof(InactivityDetectionWorker));

// Threshold and waking hours share the detection worker's config section, like the audit sample.
builder.Services.Configure<InactivityDetectionOptions>(
    configuration.GetSection($"Workers:{nameof(InactivityDetectionWorker)}"));

// Retention and look-ahead share the maintenance worker's config section, like the audit sample.
builder.Services.Configure<PartitionMaintenanceOptions>(
    configuration.GetSection($"Workers:{nameof(PartitionMaintenanceWorker)}"));

// Sample size shares the audit worker's config section — AddWorker binds only the cron from it.
builder.Services.Configure<DeviceSyncAuditOptions>(
    configuration.GetSection($"Workers:{nameof(DeviceSyncAuditWorker)}"));

// Bind to PORT env var (Cloud Run sets this to 8080)
var port = configLoader.Get(ConfigurationKeys.CloudRun.Port) ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// Health check endpoint required by Cloud Run startup probe
app.MapGet("/healthz", () => Results.Ok("healthy"));

await app.RunAsync();
