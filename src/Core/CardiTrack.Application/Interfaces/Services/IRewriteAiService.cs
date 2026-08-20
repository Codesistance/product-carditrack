namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Rewrites already-produced clinical text into caregiver-facing language. Always backed by the
/// self-hosted, non-medical rewrite model (AI:Rewrite) — see <see cref="IMedicalAiService"/> for
/// the sibling slot that produces the clinical text this service rewrites.
/// </summary>
/// <remarks>
/// Deliberately not a general-purpose text service: it exists to keep register/tone rewriting off
/// the medical model's own decode, not to open a second channel for arbitrary prompts.
/// </remarks>
public interface IRewriteAiService
{
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);

    /// <inheritdoc cref="IMedicalAiService.GenerateStructuredAsync{T}"/>
    Task<T> GenerateStructuredAsync<T>(string prompt, CancellationToken ct = default) where T : class;

    /// <inheritdoc cref="CardiTrack.Application.Interfaces.Clients.IExternalAiClient.GenerateWithUsageAsync"/>
    Task<CardiTrack.Application.DTOs.Common.AiGenerationResult<string>> GenerateWithUsageAsync(
        string prompt, CancellationToken ct = default);

    /// <inheritdoc cref="IMedicalAiService.GenerateStructuredWithUsageAsync{T}"/>
    Task<CardiTrack.Application.DTOs.Common.AiGenerationResult<T>> GenerateStructuredWithUsageAsync<T>(
        string prompt, CancellationToken ct = default) where T : class;
}
