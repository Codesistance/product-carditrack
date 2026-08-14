using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Extensions;

/// <summary>
/// Wires the pipeline's transport into the API's internal enqueue endpoint. Deliberately not
/// called from <c>AddPushServices</c>: the pipeline gets this HTTP client, not a copy of the
/// send stack (notification_engine.md §2).
/// </summary>
public static class InternalNotificationServiceExtensions
{
    public static IServiceCollection AddInternalNotificationEnqueue(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var loader = new ConfigurationLoader(configuration);
        var baseUrl = loader.GetRequired(ConfigurationKeys.Api.BaseUrl).TrimEnd('/');
        var audience = loader.GetRequired(ConfigurationKeys.Pipeline.Audience);

        services.AddHttpClient(InternalNotificationEnqueueClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).AddHttpMessageHandler(sp => new InternalApiIdentityTokenHandler(
            audience,
            sp.GetRequiredService<ILogger<InternalApiIdentityTokenHandler>>()));

        services.AddScoped<IAlertNotificationEnqueue, InternalNotificationEnqueueClient>();
        return services;
    }
}
