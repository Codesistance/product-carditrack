namespace CardiTrack.Domain.Enums;

/// <summary>
/// Which model slot served a <see cref="CardiTrack.Domain.Entities.MemberChatTurnUsage"/>
/// row's call. No <c>Public</c> member: member chat never reaches the <c>AI:Public</c> slot —
/// its clinical step stays on the in-estate MedGemma, and its non-clinical steps use the Rewrite
/// slot, whose provider is a recorded DPIA decision (row A20), not this enum. Values are
/// DB-persisted; never renumber.
/// </summary>
public enum AiProviderSlot
{
    /// <summary>MedGemma, always in-estate — <c>AI:Private</c>.</summary>
    Private = 1,

    /// <summary>The split, non-medical slot — <c>AI:Rewrite</c> (Ollama locally, Gemini via
    /// Vertex AI when deployed).</summary>
    Rewrite = 2,
}
