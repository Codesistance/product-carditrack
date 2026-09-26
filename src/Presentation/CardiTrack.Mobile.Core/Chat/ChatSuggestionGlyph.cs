namespace CardiTrack.Mobile.Core.Chat;

/// <summary>
/// The small glyph a suggested question leads with in the chat's stacked chips — what the
/// question is about, at a glance, so three stacked rows of text are not three identical shapes.
/// </summary>
/// <remarks>
/// Read from the words rather than sent by the server: the suggestions are plain strings, and a
/// glyph is decoration, so a question this does not recognise simply gets the general one. A
/// question the caregiver asked before is marked as history instead, whatever it is about — the
/// point of that row is "you asked this", not its topic.
/// </remarks>
public static class ChatSuggestionGlyph
{
    public const string History = "icon_btn_history_tint.svg";
    public const string Alert = "icon_tab_alerts.svg";
    public const string Journal = "icon_tab_journal.svg";
    public const string Sleep = "icon_dataset_sleep.svg";
    public const string Activity = "icon_dataset_activity.svg";
    public const string General = "icon_dataset_heart.svg";

    /// <summary>
    /// The server's standard questions (MemberChatService.GetSuggestionsAsync), which it uses to
    /// fill the stack when the caregiver's own recent questions run short. A list marked "recent"
    /// can carry some of these, and they are not the caregiver's history, so they keep their topic
    /// glyph. If the server's wording changes, a filler only gets the history glyph by mistake.
    /// </summary>
    public static readonly IReadOnlySet<string> StandardQuestions = new HashSet<string>(StringComparer.Ordinal)
    {
        "What's behind the current alert?",
        "Has anything come through yet?",
        "Anything I should keep an eye on?",
        "How are they doing today?",
        "Show me yesterday's Daybook",
    };

    /// <summary>Whether a chip in a list the server marked recent is one of the caregiver's own.</summary>
    public static bool IsFromHistory(string suggestion, bool listIsRecent) =>
        listIsRecent && !StandardQuestions.Contains(suggestion);

    public static string For(string suggestion, bool fromHistory)
    {
        if (fromHistory)
            return History;

        var text = suggestion.ToLowerInvariant();
        if (text.Contains("alert", StringComparison.Ordinal))
            return Alert;
        if (text.Contains("daybook", StringComparison.Ordinal) || text.Contains("journal", StringComparison.Ordinal))
            return Journal;
        if (text.Contains("sleep", StringComparison.Ordinal) || text.Contains("slept", StringComparison.Ordinal))
            return Sleep;
        if (text.Contains("active", StringComparison.Ordinal) || text.Contains("steps", StringComparison.Ordinal)
            || text.Contains("moving", StringComparison.Ordinal))
            return Activity;
        return General;
    }
}
