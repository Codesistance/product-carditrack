using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.Interfaces.Clients;

/// <summary>
/// Common interface for all AI/LLM provider clients — the seam that makes the public provider
/// swappable. Implementations are registered under the keyed DI slot "GeneralProvider" (public,
/// selected by AI:Public:Kind) or "MedicalProvider" (private, always MedGemma).
/// </summary>
public interface IExternalAiClient
{
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
    Task<string> ChatAsync(IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct = default);

    /// <summary>
    /// Forces the reply into the JSON shape of <typeparamref name="T"/> rather than free text.
    /// <paramref name="prompt"/> carries only the domain content — the implementation appends its
    /// own strict output instructions (and the schema itself) derived from <typeparamref name="T"/>,
    /// so every caller gets the same enforcement without repeating it.
    /// </summary>
    Task<T> GenerateStructuredAsync<T>(string prompt, CancellationToken ct = default) where T : class;

    /// <summary>
    /// <see cref="GenerateAsync"/>, paired with the calling model's token usage — added for member
    /// chat's per-step cost ledger rather than folded into <see cref="GenerateAsync"/> itself, so
    /// every existing caller (insights, digests, chat) keeps its current signature unchanged.
    /// </summary>
    Task<AiGenerationResult<string>> GenerateWithUsageAsync(
        string prompt, CancellationToken ct = default);

    /// <summary>
    /// <see cref="GenerateStructuredAsync{T}"/>, paired with the calling model's token usage — see
    /// <see cref="GenerateWithUsageAsync"/>.
    /// </summary>
    Task<AiGenerationResult<T>> GenerateStructuredWithUsageAsync<T>(
        string prompt, CancellationToken ct = default) where T : class;
}
