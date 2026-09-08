namespace CardiTrack.Mobile.Services;

/// <summary>
/// How far a caregiver has read on each of the CardiMember card's content buttons — CardiJournal,
/// Advise and the pending question — per member, on this device. The server carries no read
/// state for any of the three: a journal entry and a suggestion are rows that exist or don't,
/// and a question is pending until answered. The card wants one more bit than that, because
/// "there is something to look at" (the button pulses) and "you have not looked at it yet" (the
/// glyph is coloured) are different claims and the caregiver answers the second one by tapping.
/// </summary>
/// <remarks>
/// <para>
/// Kept as the content's own timestamp rather than the moment of the tap: the card is asking
/// "is what I am showing newer than what was opened?", and comparing two server instants keeps
/// a phone clock that runs behind the server from holding a just-read entry unread.
/// The question is keyed on its id instead — questions are not versioned, they are replaced.
/// </para>
/// <para>
/// Alerts are deliberately not here: acknowledging is their read state, and it lives on the
/// server (<c>DashboardResponse.UnreadAlertCount</c>), where every device sees the same answer.
/// </para>
/// <para>
/// Preferences, not SecureStorage: a timestamp and a question id say nothing about the member's
/// health, and this is exactly the per-device convenience Preferences is for.
/// </para>
/// </remarks>
public static class AttentionMarks
{
    public const string Journal = "journal";
    public const string Advise = "advise";
    public const string Question = "question";

    /// <summary>True when <paramref name="contentAtUtc"/> is newer than the last one opened for
    /// this member and kind. Nothing to show is never unread.</summary>
    public static bool IsUnread(string kind, Guid memberId, DateTime? contentAtUtc)
    {
        if (contentAtUtc is not { } contentAt)
            return false;

        var seenTicks = Preferences.Default.Get(Key(kind, memberId), 0L);
        return contentAt.Ticks > seenTicks;
    }

    /// <summary>Records that content generated at <paramref name="contentAtUtc"/> has been opened,
    /// so anything older is read from now on and only something newer will colour the glyph
    /// again. Never moves the mark backwards.</summary>
    public static void MarkSeen(string kind, Guid memberId, DateTime? contentAtUtc)
    {
        if (contentAtUtc is not { } contentAt)
            return;

        var key = Key(kind, memberId);
        if (contentAt.Ticks > Preferences.Default.Get(key, 0L))
            Preferences.Default.Set(key, contentAt.Ticks);
    }

    /// <summary>The pending question, keyed on its id: unread until this exact question was opened.</summary>
    public static bool IsQuestionUnread(Guid memberId, Guid questionId) =>
        Preferences.Default.Get(Key(Question, memberId), string.Empty) != questionId.ToString();

    public static void MarkQuestionSeen(Guid memberId, Guid questionId) =>
        Preferences.Default.Set(Key(Question, memberId), questionId.ToString());

    private static string Key(string kind, Guid memberId) => $"AttentionSeen:{kind}:{memberId:N}";
}
