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
}
