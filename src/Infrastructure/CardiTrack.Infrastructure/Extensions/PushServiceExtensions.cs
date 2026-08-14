using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Infrastructure.Diagnostics;
using CardiTrack.Infrastructure.ExternalClients.Push;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Shared;
using CardiTrack.Shared.Telemetry;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.Infrastructure.Extensions;

/// <summary>
/// Wires the push delivery spine (notification_engine.md Phase 3), split into two calls with
/// deliberately different reach.
/// </summary>
public static class PushServiceExtensions
{
    /// <summary>
    /// Just the three new repositories — every host that constructs <see cref="IUnitOfWork"/>
    /// needs these registered regardless of whether it ever sends a push, since the concrete
    /// <c>UnitOfWork</c> constructor requires them. Call this from every composition root.
    /// </summary>
    public static IServiceCollection AddPushRepositories(this IServiceCollection services)
    {
        services.AddScoped<INotificationDeliveryRepository, NotificationDeliveryRepository>();
        services.AddScoped<IPushDeviceTokenRepository, PushDeviceTokenRepository>();
        services.AddScoped<INotificationPreferenceRepository, NotificationPreferenceRepository>();
        return services;
    }

    /// <summary>
    /// The actual send stack — FirebaseApp/FCM, the ack-token HMAC service, and the dispatch/ack/
    /// preference services. Deliberately <b>not</b> called from <c>CardiTrack.PipelineJobs</c>:
    /// the AI pipeline "gets a transport, not a copy of the rules engine" (§2) — it POSTs to the
    /// internal enqueue endpoint and the API does the actual send. Not registering this here is
    /// the same boundary-by-omission pattern already used to keep the pipeline off the public AI
    /// client (see the DPIA A5 note at the top of <c>CardiTrack.PipelineJobs/Program.cs</c>) —
    /// a misconfigured environment variable must not be able to make the pipeline send push
    /// directly, any more than it can send prompts off-estate.
    /// </summary>
    public static IServiceCollection AddPushServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var configLoader = new ConfigurationLoader(configuration);

        services.AddPushRepositories();

        // Singleton: FirebaseApp owns its own credential/connection state, the same reasoning
        // AiServiceExtensions applies to the Anthropic SDK client — building one per scope would
        // churn ADC token refreshes for no benefit. ADC still resolves the *credential* from the
        // deployment environment (the Cloud Run default compute SA, already granted
        // roles/firebasecloudmessaging.admin in #108/PR176) — no service-account key file. The
        // *project ID* is passed explicitly rather than left to FirebaseApp.Create()'s own
        // discovery: that only checks GOOGLE_CLOUD_PROJECT/GCLOUD_PROJECT env vars, never the
        // metadata server, so on a cold Cloud Run instance it's a race that fails intermittently —
        // and because Worker's BackgroundServiceExceptionBehavior is StopHost, one failed
        // resolution here was enough to crash-loop the entire host (incident 2026-08-12). Passing
        // an explicit AppOptions bypasses FirebaseApp.Create()'s own ADC-credential fallback too,
        // so Credential must be set here as well — leaving it null throws "Credential must be
        // set" on every boot (same StopHost crash loop, different exception; incident 2026-08-12).
        services.AddSingleton(_ => FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
        {
            ProjectId = configLoader.GetRequired(ConfigurationKeys.Gcp.ProjectId),
            Credential = GoogleCredential.GetApplicationDefault()
        }));
        services.AddSingleton(sp => FirebaseMessaging.GetMessaging(sp.GetRequiredService<FirebaseApp>()));

        // INotificationChannel has exactly one implementation with no runtime-selectable axis —
        // unlike AI:Public's provider-kind switch, there is nothing to key this registration by.
        // Traced (see TracingProxy): the proxy span is the outer per-call boundary around the
        // existing hand-written fcm.send span, catching every call uniformly including any future
        // method this class gains that nobody remembers to hand-instrument.
        services.AddScopedWithTracing<INotificationChannel, FcmNotificationChannel>(PushTelemetry.Source);

        services.AddSingleton<IAckTokenService>(
            _ => new AckTokenService(configLoader.GetRequired(ConfigurationKeys.Notifications.AckTokenKey)));

        // DispatchService's own dependency — belongs here so every AddPushServices caller gets it.
        services.AddScoped<INotificationGapResolver, NotificationGapResolver>();

        // Traced (see TracingProxy): outer per-call boundary around the notification.enqueue/
        // .attempt spans DispatchService/AckDeliveryService already start by hand, and — unlike
        // those — covers RetryClaimedAsync's non-send early-return branches too.
        services.AddScopedWithTracing<IDispatchService, DispatchService>(PushTelemetry.Source);
        services.AddScopedWithTracing<IAckDeliveryService, AckDeliveryService>(PushTelemetry.Source);
        services.AddScoped<IDeviceTokenService, DeviceTokenService>();
        services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();
        services.AddScoped<INotificationContentService, NotificationContentService>();

        return services;
    }
}
