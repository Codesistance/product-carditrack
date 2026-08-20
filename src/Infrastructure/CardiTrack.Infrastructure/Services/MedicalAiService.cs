using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.Infrastructure.Services;

public class MedicalAiService : IMedicalAiService
{
    private readonly IExternalAiClient _client;

    public MedicalAiService([FromKeyedServices("MedicalProvider")] IExternalAiClient client)
    {
        _client = client;
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        => _client.GenerateAsync(prompt, ct);

    public Task<T> GenerateStructuredAsync<T>(string prompt, CancellationToken ct = default) where T : class
        => _client.GenerateStructuredAsync<T>(prompt, ct);

    public Task<AiGenerationResult<T>> GenerateStructuredWithUsageAsync<T>(
        string prompt, CancellationToken ct = default) where T : class
        => _client.GenerateStructuredWithUsageAsync<T>(prompt, ct);
}
