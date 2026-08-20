namespace CardiTrack.Application.DTOs.Common;

/// <summary>
/// Token accounting for one model call, as reported by the provider. All nullable because not
/// every provider path returns every field (a lenient parse degrades to absent counts, not a
/// thrown error — see <see cref="AiGenerationResult{T}"/>'s callers).
/// </summary>
public sealed record AiUsage
{
    public string? ModelName { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public long? DurationMs { get; init; }
}
