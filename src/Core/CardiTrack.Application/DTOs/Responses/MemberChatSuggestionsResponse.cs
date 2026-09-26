namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// Ready-to-send question chips for the chat's empty state — either the caregiver's own recent
/// questions about this member, or the standard set derived deterministically from the member's
/// data state. No model call either way.
/// </summary>
public class MemberChatSuggestionsResponse
{
    /// <summary><see cref="Source"/> when at least one chip is a question this caregiver asked
    /// before — the app captions the row "Recently you asked".</summary>
    public const string SourceRecent = "recent";

    /// <summary><see cref="Source"/> when every chip is from the standard set.</summary>
    public const string SourceStandard = "standard";

    public required IReadOnlyList<string> Suggestions { get; init; }

    /// <summary>
    /// Where the chips came from: <see cref="SourceRecent"/> or <see cref="SourceStandard"/>.
    /// Additive — an app that predates it just renders <see cref="Suggestions"/>, and a response
    /// that predates it reads as standard.
    /// </summary>
    public string Source { get; init; } = SourceStandard;
}
