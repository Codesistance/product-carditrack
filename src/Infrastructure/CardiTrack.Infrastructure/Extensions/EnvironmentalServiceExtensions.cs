using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.ExternalClients.Environmental;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.Infrastructure.Extensions;

/// <summary>
/// Wires the environmental-context enrichment feature (docs/llm_design.md). Deliberately called
/// only from <c>CardiTrack.PipelineJobs</c>'s <c>enrich</c> job — no other host registers
/// <see cref="IEnvironmentalContextClient"/> or <see cref="IEnvironmentalEnrichmentService"/>, so
/// no other host can call the Google Maps Platform APIs or the exercise/GPS device-client
/// methods that feed them. The same "enforced by what is not registered" boundary
/// <see cref="AiServiceExtensions.AddMedicalAiServices"/> uses for the medical AI key.
/// </summary>
public static class EnvironmentalServiceExtensions
{
    public const string HttpClientName = "EnvironmentalContextClient";

    public static IServiceCollection AddEnvironmentalContextServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(ConfigurationKeys.Environmental.SectionName)
            .Get<EnvironmentalContextSettings>() ?? new EnvironmentalContextSettings();

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException(
                $"Configuration '{ConfigurationKeys.Environmental.SectionName}:{nameof(EnvironmentalContextSettings.ApiKey)}' is not set. " +
                $"Set it in appsettings.json or as environment variable '{ConfigurationLoader.ToEnvVarKey($"{ConfigurationKeys.Environmental.SectionName}:{nameof(EnvironmentalContextSettings.ApiKey)}")}'.");
        }

        services.AddHttpClient(HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        });

        services.AddSingleton(settings);
        services.AddScoped<IEnvironmentalContextClient>(sp => new GoogleEnvironmentalClient(
            sp.GetRequiredService<IHttpClientFactory>(), settings, HttpClientName,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GoogleEnvironmentalClient>>()));

        services.AddScoped<IEnvironmentalEnrichmentService, EnvironmentalEnrichmentService>();

        return services;
    }
}
