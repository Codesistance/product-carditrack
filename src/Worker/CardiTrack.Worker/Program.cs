using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Observability;
using CardiTrack.Shared;
using CardiTrack.Worker;
using CardiTrack.Worker.Workers;
using Serilog;

// Enforce UTC for all DateTime values read from PostgreSQL timestamptz columns
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var configLoader = new ConfigurationLoader(configuration);

// LOGGING — same Serilog shape as CardiTrack.API: console always, plus APM
// shipping when the Apm engine is configured
Log.Logger = SerilogBootstrap.CreateLogger(configuration, "CardiTrack.Worker", ApmServiceNames.Worker);

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

// Distributed cache — Redis in production, in-process locally (AddCachingServices' own
// fallback). InactivityDetectionService and StatisticalAlertService use it to invalidate the
// Dashboard's cached status line when they raise or resolve an alert, and that invalidation
// only reaches the API process's cache when both sides are pointed at the same Redis instance.
builder.Services.AddCachingServices(configuration);

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
builder.Services.AddScoped<IMemberQuestionnaireRepository, MemberQuestionnaireRepository>();
builder.Services.AddScoped<IEnvironmentalReadingRepository, EnvironmentalReadingRepository>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<INotificationMuteRepository, NotificationMuteRepository>();
builder.Services.AddScoped<IAlertPreferenceRepository, AlertPreferenceRepository>();
// UnitOfWork's constructor takes every repository, so each host must register all of them even
// when it never touches the feature — chat lives in the API, but a missing registration here
// fails *every* UnitOfWork resolve, which is how notification dispatch went down in dev.
builder.Services.AddScoped<IMemberChatSessionRepository, MemberChatSessionRepository>();
builder.Services.AddScoped<IMemberChatTurnRepository, MemberChatTurnRepository>();
builder.Services.AddScoped<IMemberChatTurnUsageRepository, MemberChatTurnUsageRepository>();
builder.Services.AddScoped<INotificationSnapshotQueries, NotificationSnapshotQueries>();
builder.Services.AddPushServices(configuration);
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<ITimeSeriesPartitionService, TimeSeriesPartitionService>();

// Member photo storage — the same registration the API makes, because OrphanedPhotoCleanupWorker
// needs the bucket's list/delete port. An unset bucket is the supported local state: the adapter
// lists nothing (with one warning), so the sweep is a no-op rather than a failure.
builder.Services.AddMemberPhotoStorage(configuration);

// Application services
builder.Services.AddNumerics();
builder.Services.AddScoped<IActivityLogAggregationService, ActivityLogAggregationService>();
builder.Services.AddScoped<IInactivityDetectionService, InactivityDetectionService>();
builder.Services.AddScoped<IStatisticalAlertService, StatisticalAlertService>();
builder.Services.AddScoped<IDeviceAuthRecoveryService, DeviceAuthRecoveryService>();

// External clients
builder.Services.AddScoped<IOAuthTokenRefreshService, OAuthTokenRefreshService>();

// Fitbit provider (keyed IDeviceApiClient + keyed IDeviceSyncService)
builder.Services.AddGoogleHealthProvider();

// Background workers
builder.Services.AddWorker<WearableSyncWorker>(configuration, nameof(WearableSyncWorker));
builder.Services.AddWorker<OrphanedOrganizationCleanupWorker>(configuration, nameof(OrphanedOrganizationCleanupWorker));
builder.Services.AddWorker<BaselineCalculationWorker>(configuration, nameof(BaselineCalculationWorker));
builder.Services.AddWorker<DeviceSyncAuditWorker>(configuration, nameof(DeviceSyncAuditWorker));
builder.Services.AddWorker<PartitionMaintenanceWorker>(configuration, nameof(PartitionMaintenanceWorker));
builder.Services.AddWorker<InactivityDetectionWorker>(configuration, nameof(InactivityDetectionWorker));
builder.Services.AddWorker<StatisticalAlertWorker>(configuration, nameof(StatisticalAlertWorker));

// Ages out questions nobody got to before the day they asked about ended. The read paths already
// refuse to serve those; this is what stops one becoming a permanent placeholder for the member
// whose family never opened the app.
builder.Services.AddWorker<QuestionnaireExpiryWorker>(configuration, nameof(QuestionnaireExpiryWorker));

// Self-heal: a connection the provider refused is out of the sync rotation for good, so it needs
// a pass of its own to find out whether it can come back (DeviceAuthRecoveryService).
builder.Services.AddWorker<DeviceAuthRecoveryWorker>(configuration, nameof(DeviceAuthRecoveryWorker));

// Threshold and waking hours share the detection worker's config section, like the audit sample.
builder.Services.Configure<InactivityDetectionOptions>(
    configuration.GetSection($"Workers:{nameof(InactivityDetectionWorker)}"));
builder.Services.AddWorker<DataCompletenessWorker>(configuration, nameof(DataCompletenessWorker));

// Enforcement backstop for member photo blobs: reaps bucket objects no active member references
// (24h grace) and clears photos a crashed removal left on soft-deleted rows. DryRun shares the
// worker's config section, like the audit sample.
builder.Services.AddWorker<OrphanedPhotoCleanupWorker>(configuration, nameof(OrphanedPhotoCleanupWorker));
builder.Services.Configure<OrphanedPhotoCleanupOptions>(
    configuration.GetSection($"Workers:{nameof(OrphanedPhotoCleanupWorker)}"));

// Push delivery spine (notification_engine.md Phase 3)
builder.Services.AddWorker<NotificationDispatchWorker>(configuration, nameof(NotificationDispatchWorker));
builder.Services.Configure<PushCanaryOptions>(
    configuration.GetSection($"Workers:{nameof(PushCanaryWorker)}"));
builder.Services.AddWorker<PushCanaryWorker>(configuration, nameof(PushCanaryWorker));

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

try
{
    await app.RunAsync();
}
finally
{
    await ApmExtensions.FlushLogsAsync();
}
