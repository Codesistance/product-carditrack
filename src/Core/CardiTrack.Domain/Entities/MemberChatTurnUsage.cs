using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// Token accounting for one model call within one <see cref="MemberChatTurn"/>. A turn spans up to
/// four calls across two provider slots (<see cref="AiCallStep"/>/<see cref="AiProviderSlot"/>), so
/// usage is tracked per call rather than per turn — the granularity a future cost read needs to
/// attribute spend to a specific step, not just a conversation.
/// </summary>
/// <remarks>Deliberately not <c>ISoftDeletable</c> — see <see cref="MemberChatSession"/>. Carries no
/// prompt or completion text, only counts, so it is a materially weaker disclosure risk than the
/// turn it describes — access to it should still not be broader than access to the turn itself; see
/// the security review's token-ledger-as-side-channel finding.</remarks>
public class MemberChatTurnUsage : BaseEntity
{
    public Guid TurnId { get; set; }

    public AiCallStep Step { get; set; }

    public AiProviderSlot ProviderSlot { get; set; }

    public string ModelName { get; set; } = string.Empty;

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public long? DurationMs { get; set; }
}
