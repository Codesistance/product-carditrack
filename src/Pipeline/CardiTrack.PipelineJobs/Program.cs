using CardiTrack.Application.Interfaces.Repositories;
using System.Diagnostics;
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
builder.Services.AddScoped<IUserOrganizationRepository, UserOrganizationRepository>();
builder.Services.AddScoped<ICaregiverInviteRepository, CaregiverInviteRepository>();
builder.Services.AddScoped<IFamilyJoinRequestRepository, FamilyJoinRequestRepository>();
builder.Services.AddScoped<IDeviceConnectionRepository, DeviceConnectionRepository>();
builder.Services.AddScoped<IActivityLogRepository, ActivityLogRepository>();
builder.Services.AddScoped<IDeviceActivityLogRepository, DeviceActivityLogRepository>();
builder.Services.AddScoped<IDeviceRepository, DeviceRepository>();
builder.Services.AddScoped<IAlertRepository, AlertRepository>();
builder.Services.AddScoped<IAlertResponseRepository, AlertResponseRepository>();
builder.Services.AddScoped<IPatternBaselineRepository, PatternBaselineRepository>();
builder.Services.AddScoped<IGranularMetricRepository, GranularMetricRepository>();
builder.Services.AddScoped<IDigestRepository, DigestRepository>();
builder.Services.AddScoped<IRealtimeAssessmentRepository, RealtimeAssessmentRepository>();
builder.Services.AddScoped<IMemberQuestionnaireRepository, MemberQuestionnaireRepository>();
builder.Services.AddScoped<IEnvironmentalReadingRepository, EnvironmentalReadingRepository>();
builder.Services.AddScoped<IRhythmEpisodeRepository, RhythmEpisodeRepository>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<INotificationMuteRepository, NotificationMuteRepository>();
builder.Services.AddScoped<IAlertPreferenceRepository, AlertPreferenceRepository>();
builder.Services.AddScoped<IBenignJudgementRepository, BenignJudgementRepository>();
builder.Services.AddScoped<IMetricAlarmRepository, MetricAlarmRepository>();
builder.Services.AddScoped<IMetricAlarmStateRepository, MetricAlarmStateRepository>();
// UnitOfWork's constructor takes every repository, so each host must register all of them even
// when it never touches the feature — see the same block in the Worker's Program.cs.
builder.Services.AddScoped<IMemberChatSessionRepository, MemberChatSessionRepository>();
builder.Services.AddScoped<IMemberChatTurnRepository, MemberChatTurnRepository>();
builder.Services.AddScoped<IMemberChatTurnUsageRepository, MemberChatTurnUsageRepository>();
builder.Services.AddScoped<IMemberStatusLineRepository, MemberStatusLineRepository>();
builder.Services.AddScoped<IReportRepository, ReportRepository>();
builder.Services.AddScoped<IExportConsentRepository, ExportConsentRepository>();
builder.Services.AddScoped<ICardiMemberCreationKeyRepository, CardiMemberCreationKeyRepository>();
builder.Services.AddScoped<IPendingGrantRevocationRepository, PendingGrantRevocationRepository>();
builder.Services.AddScoped<IMemberAdviseRepository, MemberAdviseRepository>();
builder.Services.AddScoped<IMemberAdviseObservationRepository, MemberAdviseObservationRepository>();
builder.Services.AddScoped<IMemberInsightRepository, MemberInsightRepository>();
builder.Services.AddScoped<IMemberAiHoldRepository, MemberAiHoldRepository>();
builder.Services.AddScoped<IGenerationLeaseRepository, GenerationLeaseRepository>();
builder.Services.AddScoped<IDeviceHistoryRepullRepository, DeviceHistoryRepullRepository>();
builder.Services.AddScoped<IDeviceConnectionInviteRepository, DeviceConnectionInviteRepository>();
// Repositories only, not AddPushServices — the pipeline gets a transport (the internal enqueue
// endpoint, wired below for the assessor), not a copy of the send stack. See
// PushServiceExtensions.AddPushServices' remarks.
builder.Services.AddPushRepositories();
builder.Services.AddScoped<IMemberWriteGuard, MemberWriteGuard>();
builder.Services.AddScoped<IFamilyWriteGuard, FamilyWriteGuard>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// AI — the private (medical) slot only; see the header note
builder.Services.AddMedicalAiServices(configuration);
builder.Services.AddNumerics();
builder.Services.AddScoped<IDigestGenerationService, DigestGenerationService>();
builder.Services.AddScoped<IRealtimeAssessmentService, RealtimeAssessmentService>();
// The R1 statistical findings judgement — the eleven rules compute, MedGemma decides. Here rather
// than the Worker because it calls the medical model (CLAUDE.md: AI inference is pipeline work).
builder.Services.AddScoped<IStatisticalAlertService, StatisticalAlertService>();
// The chat theming pass — Rewrite slot only, which AddMedicalAiServices above already carries.
builder.Services.AddScoped<IChatThemeService, ChatThemeService>();

// Device provider — the aggregator's targeted sync is the same SyncCardiMemberAsync the Worker
// runs, so this host carries the same provider wiring (incl. the Fitbit OAuth credentials the
// token refresh needs). Still no public AI key.
builder.Services.Configure<List<DeviceProviderSettings>>(
    configuration.GetSection(DeviceProviderSettings.SectionName));
builder.Services.AddScoped<IOAuthTokenRefreshService, OAuthTokenRefreshService>();
builder.Services.AddScoped<CardiTrack.Application.Interfaces.Clients.IOAuthGrantRevoker, OAuthGrantRevoker>();
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

// The job's root span. Declared out here, rather than as a `using var` inside the try, so the
// catch below can mark it failed: a span that ends green on a run that exited non-zero is worse
// than no span, because it is the one an alert would trust.
//
// Until this existed, every arm but `notify` produced a scatter of parentless spans — one per
// MedGemma call, one per Npgsql command — with nothing tying them to a pass, a member or a rule,
// and none of the arm's log lines carried a trace_id, because ActivityLogEnricher reads
// Activity.Current and a job that starts no activity has none. The AI and database spans were
// always shipping; what was missing was something to hang them from.
Activity? jobActivity = null;

// No app.Run(): a job executes one pass and exits, and never listens.
try
{
    // Nothing starts the host, and AddOpenTelemetry builds its providers from a hosted service —
    // so without this the tracer is first constructed by ForceFlushTelemetry in the finally
    // below, after the work it existed to trace. A process with no provider has no
    // ActivityListener, which makes every StartActivity return null: no spans from this service
    // at all, and no trace_id on any of its log lines. Both ends of the job's telemetry life are
    // explicit for that reason.
    //
    // Inside the try, and first: building a provider runs the exporter configuration, which can
    // throw on a malformed endpoint. Outside, that would be an unhandled exception on a path
    // whose whole purpose is to exit non-zero with a fatal log explaining why.
    app.Services.StartTelemetry();

    // After StartTelemetry, necessarily: before the provider is resolved there is no
    // ActivityListener, so this would return null and the whole run would go untraced.
    jobActivity = PipelineTelemetry.Source.StartActivity($"pipeline.{jobName}", ActivityKind.Internal);
    jobActivity?.SetTag("pipeline.job", jobName);

    Log.Information("PipelineJobs run starting: {Job}.", jobName);

    using var scope = app.Services.CreateScope();
    switch (jobName)
    {
        case "digest":
            var digests = scope.ServiceProvider.GetRequiredService<IDigestGenerationService>();
            var generated = await digests.GenerateDueDigestsAsync(DateTime.UtcNow);

            // The daybook entry rides this job rather than owning one. It is due once per member per
            // day, at 02:00 in that member's own local time — so a job of its own would need either
            // a Cloud Scheduler entry per timezone the fleet spans, or exactly the same per-member
            // due-check this one already has to do, on top of a second Cloud Run job, a second
            // schedule and a second cold start. What it costs here is one indexed read per member
            // per pass, and one extra inference per member per day.
            var reviews = await digests.GenerateDueDaybooksAsync(DateTime.UtcNow);

            // The Weekbook rides the same pass for the same reasons, and costs less: it is due on
            // one local weekday, so on six days in seven each member is declined on a date
            // comparison, before any week-scoped read. It cannot skip the pass wholesale the way
            // the Monthbook does — members choose their own week start, so at any instant some
            // weekday somewhere is a week start.
            var weekbooks = await digests.GenerateDueWeekbooksAsync(DateTime.UtcNow);

            // And the Monthbook, cheapest of the three: on the days when no timezone on earth is
            // on the first of a month — about twenty-nine in thirty — it answers without reading
            // anything at all.
            var monthbooks = await digests.GenerateDueMonthbooksAsync(DateTime.UtcNow);

            // The weekly and monthly trend narratives ride this pass too, and for the reason the
            // books do rather than one of their own: they fall due on the member's own local
            // weekday and hour, and `--job trend` — which still owns the rolling narrative — runs
            // once a day, so it would see each member at a single local instant and miss anyone
            // whose chosen hour had not passed at it. Same due check as the books, shared through
            // JournalDueCheck, so a week's narrative and that week's Weekbook always describe the
            // same seven days.
            var journalTrends = scope.ServiceProvider.GetRequiredService<TrendInterpretationService>();
            var narratives = await journalTrends.InterpretDueJournalHorizonsAsync(DateTime.UtcNow);

            Log.Information(
                "PipelineJobs run finished. Digests generated: {Generated}, daybook entries written: "
                + "{Reviews}, weekbooks written: {Weekbooks}, monthbooks written: {Monthbooks}, "
                + "journal trend narratives written: {Narratives}.",
                generated, reviews, weekbooks, monthbooks, narratives);
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
            // The daily statistical findings ride the same pass: the eleven R1 rules produce
            // findings (nine against the 30-day baseline, two from the device's own rhythm
            // classifications), and MedGemma — already warm from the
            // assessor — returns a severity and a clinical read for each. What a caregiver reads
            // is written from those reads by the Rewrite slot, in one further call. Runs before
            // the digest pass below so a summary written on this execution already sees the alerts.
            //
            // Cost: one private-slot call per member per pass in which a finding survives cooldown
            // and dedup, none for a member with nothing off, and one Rewrite-slot call on top in
            // the passes where at least one read clears Yellow — a pass whose findings are all
            // judged benign still costs the one call it always did. A raised alert dedups its rule
            // for the day; a benign verdict is remembered in BenignJudgements against a hash of
            // the finding's own figures, so the same question is put to the model once rather than
            // on all 288 passes. Every rule is remembered and none is exempt: readings that move
            // make a different hash and are judged again, no_morning_activity names the clock in
            // its observation and so re-judges every pass on its own, and the measured rules carry
            // the device's counts. See StatisticalAlertService's remarks.
            var judgements = scope.ServiceProvider.GetRequiredService<IStatisticalAlertService>();
            var judged = await judgements.EvaluateAsync(DateTime.UtcNow);
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
                "PipelineJobs run finished. Assessments written: {Assessed}, statistical alerts raised: "
                + "{Judged}, summaries written: {Generated}.",
                assessed, judged, generatedAfterAssess);
            return 0;

        case "enrich":
            var enrichment = scope.ServiceProvider.GetRequiredService<IEnvironmentalEnrichmentService>();
            var enriched = await enrichment.EnrichDueSessionsAsync(DateTime.UtcNow);
            Log.Information("PipelineJobs run finished. Environmental readings written: {Enriched}.", enriched);
            return 0;

        case "trend":
            // The daily trend narrative (docs/llm_design.md — "Trend interpretation pipeline"),
            // and what replaced the dropped per-user LSTM. Every figure it reads was computed in
            // .NET from the member's own readings; the model's only job is to say what they say,
            // against the pinned reference table. One call per member with a month of history
            // whose narrative is older than the day, and none at all for a member still learning.
            var trends = scope.ServiceProvider.GetRequiredService<TrendInterpretationService>();
            var narrated = await trends.InterpretDueMembersAsync();
            Log.Information("PipelineJobs run finished. Trend narratives written: {Narrated}.", narrated);
            return 0;

        case "theme":
            // Labels completed member-chat conversations for the history list — one Rewrite-slot
            // call per unthemed conversation, batch-capped; see ChatThemeService.
            var themer = scope.ServiceProvider.GetRequiredService<IChatThemeService>();
            var themed = await themer.ThemeDueSessionsAsync(DateTime.UtcNow);
            Log.Information("PipelineJobs run finished. Chat conversations themed: {Themed}.", themed);
            return 0;

        default:
            // Non-zero exit without an exception, so the catch below never runs and the root span
            // would otherwise end unset — a failed Cloud Run execution reading as a clean trace.
            jobActivity?.SetStatus(ActivityStatusCode.Error, $"unknown job '{jobName}'");
            Log.Fatal("Unknown job '{Job}'. Known jobs: digest, aggregate, assess, enrich, trend, theme.", jobName);
            return 1;
    }
}
catch (Exception ex)
{
    // A non-zero exit marks the execution failed in Cloud Run, which is what alerting keys on.
    jobActivity?.AddException(ex);
    jobActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
    Log.Fatal(ex, "PipelineJobs run failed: {Job}.", jobName);
    return 1;
}
finally
{
    // Before the flushes: a span still open when its provider is flushed is a span that never
    // ships, and this is the one the rest of the run's telemetry hangs from.
    jobActivity?.Dispose();

    // Guarded, and first, so the two flushes cannot take each other down. Both resolve providers
    // that may be the very thing that failed above, and an exception thrown here would replace
    // the outcome the catch just recorded — losing the fatal log that explains the run, which is
    // the one thing this block exists to deliver.
    try
    {
        app.Services.ForceFlushTelemetry();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "PipelineJobs could not flush telemetry on exit; logs still follow.");
    }

    await ApmExtensions.FlushLogsAsync();
}
