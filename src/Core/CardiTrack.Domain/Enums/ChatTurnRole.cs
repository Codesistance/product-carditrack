namespace CardiTrack.Domain.Enums;

/// <summary>Who wrote one turn of a <see cref="CardiTrack.Domain.Entities.MemberChatSession"/>.</summary>
public enum ChatTurnRole
{
    /// <summary>The caregiver's question.</summary>
    User = 1,

    /// <summary>The rewritten, caregiver-facing answer.</summary>
    Assistant = 2,
}
