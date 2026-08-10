using CardiTrack.HealthWebhookReceiver;
using CardiTrack.Observability;
using CardiTrack.Shared;
using Google.Cloud.PubSub.V1;
using Serilog;

// The platform's only public-ingress pipeline surface (docs/llm_design.md
// `HealthWebhookReceiver`): Google Health API webhook notifications arrive here, are
// authenticated against the Subscriber's shared secret, acknowledged with 204, and forwarded raw
// to Pub/Sub. Nothing is parsed and nothing else is reachable from this process — no database,
// no AI, no business logic. The 5-minute aggregator (a later slice) consumes the topic and
// re-fetches the actual data (notify-then-fetch), so this payload is never trusted.

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var configLoader = new ConfigurationLoader(configuration);

// LOGGING — same Serilog shape as the other hosts
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithProperty("Application", "CardiTrack.HealthWebhookReceiver")
    .Enrich.WithProperty("Version", DeploymentInfo.Version)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .AddApmShipping(configuration.GetApmOptions(), ApmServiceNames.WebhookReceiver)
    .CreateLogger();

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

// Conservative verification answer: the discovery document says the endpoint "will be verified"
// using the Subscriber secret but does not document the handshake's shape, so a plain GET
// answers 200. Expect to adjust this on live Subscriber registration — the same "(assumed),
// pending live check" convention FitbitApiClient used before sandbox access.
app.MapGet("/webhooks/google-health", () => Results.Ok());

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
