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
using CardiTrack.PipelineJobs.Notifications;
using CardiTrack.Shared;
using Google.Cloud.PubSub.V1;
using Serilog;

// The AI pipeline's scheduled work, run as a Cloud Run *job*: Cloud Scheduler triggers an
// execution, the job does exactly one pass of its work and exits, and the exit code is the
// job's verdict. This host is the sanctioned home for LLM background work per CLAUDE.md —
// digests are AI-pipeline responsibilities and must not run in CardiTrack.Worker.
//
// Wired through AddMedicalAiServices, deliberately: this host holds no public-provider key and
// no public client, so it physically cannot send health data off-estate (the DPIA A5 boundary,
// enforced by what is not registered).

// Enforce UTC for all DateTime values read from PostgreSQL timestamptz columns
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var configLoader = new ConfigurationLoader(configuration);

// LOGGING — same Serilog shape as the other hosts: console always, plus APM shipping when
// the Apm engine is configured
Log.Logger = SerilogBootstrap.CreateLogger(configuration, "CardiTrack.PipelineJobs", ApmServiceNames.PipelineJobs);

builder.Host.UseSerilog();

// APM TRACING — no-op until Apm__Engine + Apm__Data are configured
builder.AddApmTracing(ApmServiceNames.PipelineJobs);

// Database
builder.Services.AddDbContext<CardiTrackDbContext>(options =>
    options.UseCardiTrackNpgsql(configLoader.Get(ConfigurationKeys.ConnectionStrings.DefaultConnection)));

// Encryption — required by the persistence layer's encrypted converters; built eagerly so a
// missing key stops the job at startup rather than mid-run.
builder.Services.AddSingleton(configLoader);
builder.Services.AddSingleton<IEncryptionService>(
    new AesEncryptionService(configLoader.GetRequired(ConfigurationKeys.Encryption.Key)));

// Repositories — the full unit of work, matching the other composition roots
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
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<INotificationMuteRepository, NotificationMuteRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// AI — the private (medical) slot only; see the header note
builder.Services.AddMedicalAiServices(configuration);
builder.Services.AddScoped<IDigestGenerationService, DigestGenerationService>();
builder.Services.AddScoped<IRealtimeAssessmentService, RealtimeAssessmentService>();

// Device provider — the aggregator's targeted sync is the same SyncCardiMemberAsync the Worker
// runs, so this host carries the same provider wiring (incl. the Fitbit OAuth credentials the
// token refresh needs). Still no public AI key.
builder.Services.Configure<List<DeviceProviderSettings>>(
    configuration.GetSection(DeviceProviderSettings.SectionName));
builder.Services.AddScoped<IOAuthTokenRefreshService, OAuthTokenRefreshService>();
builder.Services.AddScoped<IActivityLogAggregationService, ActivityLogAggregationService>();
builder.Services.AddFitbitProvider();

// One binary, several jobs: Cloud Run job resources share the image and select via
// `--job <name>` in their container args. No arg means digest, the first job this host ran.
var jobArgIndex = Array.IndexOf(args, "--job");
var jobName = jobArgIndex >= 0 && jobArgIndex + 1 < args.Length ? args[jobArgIndex + 1] : "digest";

// Realtime subscription — the aggregator's input, wired only for the aggregator: each job
// resource carries only its own env vars, and required-config reads execute at startup, so an
// unconditional read here would crash the digest job over settings it does not have.
if (jobName == "aggregate")
{
    var subscriptionName = SubscriptionName.FromProjectSubscription(
        configLoader.GetRequired(ConfigurationKeys.PubSub.ProjectId),
        configLoader.GetRequired(ConfigurationKeys.PubSub.SubscriptionId));
    builder.Services.AddSingleton(await SubscriberServiceApiClient.CreateAsync());
    builder.Services.AddSingleton<INotificationSource>(sp =>
        new PubSubNotificationSource(sp.GetRequiredService<SubscriberServiceApiClient>(), subscriptionName));
    builder.Services.AddScoped<INotificationDrainService, NotificationDrainService>();
}

var app = builder.Build();

// No app.Run(): a job executes one pass and exits, and never listens.
try
{
    Log.Information("PipelineJobs run starting: {Job}.", jobName);

    using var scope = app.Services.CreateScope();
    switch (jobName)
    {
        case "digest":
            var digests = scope.ServiceProvider.GetRequiredService<IDigestGenerationService>();
            var generated = await digests.GenerateDueDigestsAsync(DateTime.UtcNow);
            Log.Information("PipelineJobs run finished. Digests generated: {Generated}.", generated);
            return 0;

        case "aggregate":
            var drain = scope.ServiceProvider.GetRequiredService<INotificationDrainService>();
            var summary = await drain.DrainAsync();
            Log.Information(
                "PipelineJobs run finished. Notifications: {Messages}, connections synced: {Synced}, "
                + "unknown users: {Unknown}, unparseable: {Unparseable}, failed users: {Failed}.",
                summary.Messages, summary.SyncedConnections, summary.UnknownUsers,
                summary.Unparseable, summary.FailedUsers);
            return 0;

        case "assess":
            var assessments = scope.ServiceProvider.GetRequiredService<IRealtimeAssessmentService>();
            var assessed = await assessments.AssessDueMembersAsync(DateTime.UtcNow);
            Log.Information("PipelineJobs run finished. Assessments written: {Assessed}.", assessed);
            return 0;

        default:
            Log.Fatal("Unknown job '{Job}'. Known jobs: digest, aggregate, assess.", jobName);
            return 1;
    }
}
catch (Exception ex)
{
    // A non-zero exit marks the execution failed in Cloud Run, which is what alerting keys on.
    Log.Fatal(ex, "PipelineJobs run failed: {Job}.", jobName);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
