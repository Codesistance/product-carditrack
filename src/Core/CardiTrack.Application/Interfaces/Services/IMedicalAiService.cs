using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// High-level service for medical analysis tasks.
/// Always backed by the self-hosted MedGemma service — the provider is fixed in code, not
/// configurable, so health data stays in-project.
/// </summary>
public interface IMedicalAiService
{
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);

    /// <summary>Forces the reply into the JSON shape of <typeparamref name="T"/> — see
    /// <see cref="Clients.IExternalAiClient.GenerateStructuredAsync{T}"/>.</summary>
    Task<T> GenerateStructuredAsync<T>(string prompt, CancellationToken ct = default) where T : class;

    /// <inheritdoc cref="Clients.IExternalAiClient.GenerateStructuredWithUsageAsync{T}"/>
    Task<AiGenerationResult<T>> GenerateStructuredWithUsageAsync<T>(
        string prompt, CancellationToken ct = default) where T : class;
}
