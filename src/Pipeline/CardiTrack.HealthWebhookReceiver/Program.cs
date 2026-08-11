using CardiTrack.HealthWebhookReceiver;
using CardiTrack.Observability;
using CardiTrack.Shared;
using Google.Cloud.PubSub.V1;
using Serilog;

// The platform's only public-ingress pipeline surface (docs/llm_design.md
// `HealthWebhookReceiver`): Google Health API webhook notifications arrive here, are
// authenticated against the Subscriber's shared secret, acknowledged with 200, and forwarded raw
// to Pub/Sub. Nothing is parsed — with one documented exception: the handler peeks just far
// enough to recognise Google's {"type": "verification"} handshake probe and drop it instead of
// forwarding. Nothing else is reachable from this process — no database, no AI, no business
// logic. The 5-minute aggregator consumes the topic and re-fetches the actual data
// (notify-then-fetch), so this payload is never trusted.
//
// The Subscriber's registered endpointUri must be THIS path-qualified URL
// (https://<service>/webhooks/google-health) — registering the service root sends Google's
// verification probes into a 404.

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var configLoader = new ConfigurationLoader(configuration);

// LOGGING — same Serilog shape as the other hosts
Log.Logger = SerilogBootstrap.CreateLogger(
    configuration, "CardiTrack.HealthWebhookReceiver", ApmServiceNames.WebhookReceiver);

builder.Host.UseSerilog();
builder.AddApmTracing(ApmServiceNames.WebhookReceiver);

// A notification is a small JSON body; anything past this cap is not a notification.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);

// Required config is resolved at startup, so a missing secret or topic stops the revision
// instead of surfacing as the first notification's 500.
var webhookSecret = configLoader.GetRequired(ConfigurationKeys.Webhook.Secret);
var topicName = TopicName.FromProjectTopic(
    configLoader.GetRequired(ConfigurationKeys.PubSub.ProjectId),
    configLoader.GetRequired(ConfigurationKeys.PubSub.TopicId));

// Singleton: the client owns connection state and batching.
builder.Services.AddSingleton(await new PublisherClientBuilder { TopicName = topicName }.BuildAsync());
builder.Services.AddSingleton<INotificationPublisher, PubSubNotificationPublisher>();
builder.Services.AddSingleton(sp =>
    new WebhookNotificationHandler(webhookSecret, sp.GetRequiredService<INotificationPublisher>()));

var port = configLoader.Get(ConfigurationKeys.CloudRun.Port) ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok("healthy"));

// Verification is POST-based (documented, superseding the earlier assumed-GET contract):
// Google sends two {"type": "verification"} probes on Subscriber create/update — one with the
// registered secret expecting 200/201, one unauthorized expecting 401/403 — so the POST
// handler below serves the handshake and real notifications alike.
app.MapPost("/webhooks/google-health", async (HttpRequest request, WebhookNotificationHandler handler, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync(ct);

    var status = await handler.HandleAsync(
        request.Headers.Authorization.ToString(),
        body,
        request.ContentType,
        DateTime.UtcNow,
        ct);

    return Results.StatusCode(status);
});

app.Run();
