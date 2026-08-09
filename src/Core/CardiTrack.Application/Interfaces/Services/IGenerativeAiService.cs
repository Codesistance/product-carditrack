using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// High-level service for report generation and conversational AI.
/// Backed by the public, off-estate provider selected by AI:Public:Kind — so prompts built for it
/// must not carry anything that identifies a member.
/// </summary>
public interface IGenerativeAiService
{
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
    Task<string> ChatAsync(IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct = default);
}
