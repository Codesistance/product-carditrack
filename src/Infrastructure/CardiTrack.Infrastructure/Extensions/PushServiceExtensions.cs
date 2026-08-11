using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Infrastructure.ExternalClients.Push;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Security;
using CardiTrack.Shared;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
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
        // churn ADC token refreshes for no benefit. FirebaseApp.Create() with no options resolves
        // ADC from the deployment environment (the Cloud Run default compute SA, already granted
        // roles/firebasecloudmessaging.admin in #108/PR176) — no service-account key file.
        services.AddSingleton(_ => FirebaseApp.DefaultInstance ?? FirebaseApp.Create());
        services.AddSingleton(sp => FirebaseMessaging.GetMessaging(sp.GetRequiredService<FirebaseApp>()));

        // INotificationChannel has exactly one implementation with no runtime-selectable axis —
        // unlike AI:Public's provider-kind switch, there is nothing to key this registration by.
        services.AddScoped<INotificationChannel, FcmNotificationChannel>();

        services.AddSingleton<IAckTokenService>(
            _ => new AckTokenService(configLoader.GetRequired(ConfigurationKeys.Notifications.AckTokenKey)));

        // DispatchService's own dependency, not registered by every AddPushServices caller
        // previously — CardiTrack.Worker got AddPushServices without it and crashed the whole
        // IHost (BackgroundServiceExceptionBehavior=StopHost) the first time
        // NotificationDispatchWorker's escalation sweep tried to resolve IDispatchService.
        services.AddScoped<INotificationGapResolver, NotificationGapResolver>();

        services.AddScoped<IDispatchService, DispatchService>();
        services.AddScoped<IAckDeliveryService, AckDeliveryService>();
        services.AddScoped<IDeviceTokenService, DeviceTokenService>();
        services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();
        services.AddScoped<INotificationContentService, NotificationContentService>();

        return services;
    }
}
