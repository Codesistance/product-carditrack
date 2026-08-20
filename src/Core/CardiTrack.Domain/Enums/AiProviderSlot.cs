namespace CardiTrack.Domain.Enums;

/// <summary>
/// Which in-estate model slot served a <see cref="CardiTrack.Domain.Entities.MemberChatTurnUsage"/>
/// row's call. No <c>Public</c> member: member chat never reaches an external provider — see
/// <c>MemberChatService</c>.
/// </summary>
public enum AiProviderSlot
{
    /// <summary>MedGemma — <c>AI:Private</c>.</summary>
    Private = 1,

    /// <summary>The split, non-medical Gemma3 slot — <c>AI:Rewrite</c>.</summary>
    Rewrite = 2,
}
