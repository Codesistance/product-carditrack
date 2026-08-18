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

// Encryption — applied by the services that own each protected field (there are no EF value
// converters; see docs/technical/data_protection_architecture.md). Built eagerly so a missing
// key stops the job at startup rather than mid-run, which now includes the digest job's
// questionnaire writes as well as every read of a caregiver note.
builder.Services.AddSingleton(configLoader);
builder.Services.AddSingleton<IEncryptionService>(
    new AesEncryptionService(configLoader.GetRequired(ConfigurationKeys.Encryption.Key)));

// Distributed cache — Redis in production, in-process locally (AddCachingServices' own
// fallback). RealtimeAssessmentService uses it to invalidate the Dashboard's cached status line
// when it raises or resolves an alert, and that invalidation only reaches the API process's
// cache when both sides are pointed at the same Redis instance.
builder.Services.AddCachingServices(configuration);

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
builder.Services.AddScoped<IMemberQuestionnaireRepository, MemberQuestionnaireRepository>();
builder.Services.AddScoped<IEnvironmentalReadingRepository, EnvironmentalReadingRepository>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<INotificationMuteRepository, NotificationMuteRepository>();
builder.Services.AddScoped<IAlertPreferenceRepository, AlertPreferenceRepository>();
// Repositories only, not AddPushServices — the pipeline gets a transport (the internal enqueue
// endpoint, wired below for the assessor), not a copy of the send stack. See
// PushServiceExtensions.AddPushServices' remarks.
builder.Services.AddPushRepositories();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// AI — the private (medical) slot only; see the header note
builder.Services.AddMedicalAiServices(configuration);
builder.Services.AddNumerics();
builder.Services.AddScoped<IDigestGenerationService, DigestGenerationService>();
builder.Services.AddScoped<IRealtimeAssessmentService, RealtimeAssessmentService>();

// Device provider — the aggregator's targeted sync is the same SyncCardiMemberAsync the Worker
// runs, so this host carries the same provider wiring (incl. the Fitbit OAuth credentials the
// token refresh needs). Still no public AI key.
builder.Services.Configure<List<DeviceProviderSettings>>(
    configuration.GetSection(DeviceProviderSettings.SectionName));
builder.Services.AddScoped<IOAuthTokenRefreshService, OAuthTokenRefreshService>();
builder.Services.AddScoped<IActivityLogAggregationService, ActivityLogAggregationService>();
builder.Services.AddGoogleHealthProvider();

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

// Environmental enrichment — wired only for its own job, mirroring the aggregator's Pub/Sub
// wiring above: the Google Maps Platform key and the exercise/GPS device-client methods it
// drives are registered nowhere else in this process (EnvironmentalServiceExtensions).
if (jobName == "enrich")
{
    builder.Services.AddEnvironmentalContextServices(configuration);
}

// Assessor-only: orange/red alerts POST to the API's internal enqueue endpoint. Digest and
// aggregator never raise those alerts, so they must not require Api:BaseUrl / Pipeline:Audience
// at startup. The send stack itself stays in the API (AddPushServices is still not called).
if (jobName == "assess")
{
    builder.Services.AddInternalNotificationEnqueue(configuration);
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

            // The day review rides this job rather than owning one. It is due once per member per
            // day, at 02:00 in that member's own local time — so a job of its own would need either
            // a Cloud Scheduler entry per timezone the fleet spans, or exactly the same per-member
            // due-check this one already has to do, on top of a second Cloud Run job, a second
            // schedule and a second cold start. What it costs here is one indexed read per member
            // per pass, and one extra inference per member per day.
            var reviews = await digests.GenerateDueDayReviewsAsync(DateTime.UtcNow);

            Log.Information(
                "PipelineJobs run finished. Digests generated: {Generated}, day reviews written: {Reviews}.",
                generated, reviews);
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
            // The digest job still runs at :00/:30; this pass runs every 5 minutes, two minutes
            // after the aggregator. An hour the assessor has just called a problem would otherwise
            // sit behind a summary written on the half-hour until the next digest tick. Generation
            // still no-ops members whose readings have not become worth rewriting (the hourly
            // floor — wider early in the day, and never lifting overnight — waived throughout for
            // problem samples, baseline divergence and jumps). That no-op is the common case by a
            // wide margin: this pass runs 288 times a day and writes single-digit assessments.
            var digestAfterAssess = scope.ServiceProvider.GetRequiredService<IDigestGenerationService>();
            var generatedAfterAssess = await digestAfterAssess.GenerateDueDigestsAsync(DateTime.UtcNow);
            Log.Information(
                "PipelineJobs run finished. Assessments written: {Assessed}, summaries written: {Generated}.",
                assessed, generatedAfterAssess);
            return 0;

        case "enrich":
            var enrichment = scope.ServiceProvider.GetRequiredService<IEnvironmentalEnrichmentService>();
            var enriched = await enrichment.EnrichDueSessionsAsync(DateTime.UtcNow);
            Log.Information("PipelineJobs run finished. Environmental readings written: {Enriched}.", enriched);
            return 0;

        default:
            Log.Fatal("Unknown job '{Job}'. Known jobs: digest, aggregate, assess, enrich.", jobName);
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
    app.Services.ForceFlushTelemetry();
    await ApmExtensions.FlushLogsAsync();
}
