namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// High-level service for medical analysis tasks.
/// Always backed by the self-hosted MedGemma service — the provider is fixed in code, not
/// configurable, so health data stays in-project.
/// </summary>
public interface IMedicalAiService
{
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
}
