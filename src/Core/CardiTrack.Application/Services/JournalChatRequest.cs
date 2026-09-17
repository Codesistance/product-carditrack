using System.Globalization;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>What a caregiver asked the chat to do to the CardiJournal.</summary>
public enum JournalChatAction
{
    /// <summary>Read one book back.</summary>
    Show = 1,

    /// <summary>Name the recent books of one kind.</summary>
    List = 2,

    /// <summary>Delete one book. Confirmed on the next turn.</summary>
    Discard = 3,

    /// <summary>Write one book again from its period's readings. Confirmed on the next turn.</summary>
    Rewrite = 4,
}

/// <summary>
/// A resolved journal ask: the action, the book, and the period the book covers — dated, like the
/// stored book, by the period's last day. Null <see cref="PeriodEnd"/> means the caregiver named
/// no period.
/// </summary>
/// <remarks>
/// <para>
/// The date arithmetic lives here, in code, and not in the model that reads the caregiver's
/// words. The resolution call is asked for <em>any</em> day inside the period meant; snapping
/// that day onto the week the member's journal actually uses, or onto the month's last day, is
/// arithmetic with one right answer, and asking a model for it would be asking it to know the
/// member's week start.
/// </para>
/// <para>
/// Also the confirmation vocabulary and the one-line serialisation the session carries between
/// the offer and the yes — both closed, both matched in code, so the confirming turn costs no
/// model call and cannot be argued into a different action than the one offered.
/// </para>
/// </remarks>
public sealed record JournalChatRequest(JournalChatAction Action, DigestAudience Audience, DateOnly? PeriodEnd)
{
    /// <summary>The labels the resolution call may answer with, in the order they are offered.</summary>
    public static IReadOnlyList<string> ActionLabels { get; } = ["show", "list", "discard", "rewrite"];

    /// <summary>The book labels the resolution call may answer with.</summary>
    public static IReadOnlyList<string> CadenceLabels { get; } = ["day", "week", "month"];

    /// <summary>Deleting or writing over a book: offered first, done on the next turn.</summary>
    public bool IsDestructive => Action is JournalChatAction.Discard or JournalChatAction.Rewrite;

    public static JournalChatAction? ParseAction(string? label) => Canonical(label) switch
    {
        "show" => JournalChatAction.Show,
        "list" => JournalChatAction.List,
        "discard" or "delete" or "remove" => JournalChatAction.Discard,
        "rewrite" or "regenerate" or "recreate" or "redo" => JournalChatAction.Rewrite,
        _ => null,
    };

    public static DigestAudience? ParseCadence(string? label) => Canonical(label) switch
    {
        "day" or "daybook" or "daily" => DigestAudience.Daybook,
        "week" or "weekbook" or "weekly" => DigestAudience.Weekbook,
        "month" or "monthbook" or "monthly" => DigestAudience.Monthbook,
        _ => null,
    };

    /// <summary>A yyyy-MM-dd the model wrote, or null for anything else — a nonsense date costs
    /// the period, never the turn.</summary>
    public static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(
            value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>
    /// The last day of the period of <paramref name="audience"/>'s kind that contains
    /// <paramref name="anyDayInside"/> — the date the stored book carries.
    /// </summary>
    /// <param name="weekStartsOn">The member's journal week start; a week ends the day before it.</param>
    public static DateOnly PeriodEndContaining(DateOnly anyDayInside, DigestAudience audience, DayOfWeek weekStartsOn)
    {
        switch (audience)
        {
            case DigestAudience.Daybook:
                return anyDayInside;

            case DigestAudience.Weekbook:
                var weekLastDay = (DayOfWeek)(((int)weekStartsOn + 6) % 7);
                var daysAhead = ((int)weekLastDay - (int)anyDayInside.DayOfWeek + 7) % 7;
                return anyDayInside.AddDays(daysAhead);

            case DigestAudience.Monthbook:
                return new DateOnly(
                    anyDayInside.Year, anyDayInside.Month, DateTime.DaysInMonth(anyDayInside.Year, anyDayInside.Month));

            default:
                throw new ArgumentOutOfRangeException(nameof(audience), audience, "Not a CardiJournal book.");
        }
    }

    /// <summary>The first day of the period ending on <paramref name="periodEnd"/>.</summary>
    public static DateOnly PeriodStart(DateOnly periodEnd, DigestAudience audience) => audience switch
    {
        DigestAudience.Daybook => periodEnd,
        DigestAudience.Weekbook => periodEnd.AddDays(-6),
        DigestAudience.Monthbook => new DateOnly(periodEnd.Year, periodEnd.Month, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "Not a CardiJournal book."),
    };

    /// <summary>
    /// Whether the period ending on <paramref name="periodEnd"/> is over, seen from the member's
    /// local <paramref name="localToday"/>. How far <em>back</em> a book can reach is deliberately
    /// not decided here — the stored books and the readings answer that, and a constant here would
    /// have to agree with the partition worker's retention setting forever.
    /// </summary>
    public static bool IsFinished(DateOnly periodEnd, DateOnly localToday) => periodEnd < localToday;

    // ── The pending-action line the session carries ────────────────────────

    /// <summary>
    /// The request as one short line for <c>MemberChatSession.PendingAction</c>. Only destructive
    /// requests with a period are ever pending, so both are required.
    /// </summary>
    public string Serialize()
    {
        if (!IsDestructive || PeriodEnd is null)
            throw new InvalidOperationException("Only a dated discard or rewrite is held for confirmation.");

        return string.Join(
            '|', Action.ToString(), Audience.ToString(), PeriodEnd.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    /// <summary>The request a stored line describes, or null for anything malformed — a session
    /// written by an older build simply has nothing pending.</summary>
    public static JournalChatRequest? TryDeserialize(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        var parts = stored.Split('|');
        if (parts.Length != 3
            || !Enum.TryParse<JournalChatAction>(parts[0], ignoreCase: false, out var action) || !Enum.IsDefined(action)
            || !Enum.TryParse<DigestAudience>(parts[1], ignoreCase: false, out var audience)
            || audience is not (DigestAudience.Daybook or DigestAudience.Weekbook or DigestAudience.Monthbook)
            || ParseDate(parts[2]) is not { } periodEnd)
        {
            return null;
        }

        var request = new JournalChatRequest(action, audience, periodEnd);
        return request.IsDestructive ? request : null;
    }

    // ── The confirmation vocabulary ────────────────────────────────────────

    private static readonly HashSet<string> Affirmatives = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "y", "yes please", "yep", "yeah", "yup", "ok", "okay", "sure", "go ahead", "yes go ahead",
        "do it", "yes do it", "please do", "please", "confirm", "confirmed", "go on", "proceed",
        "yes proceed", "that's right", "thats right", "correct", "fine", "alright", "all right",
    };

    private static readonly HashSet<string> Negatives = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "n", "nope", "no thanks", "no thank you", "don't", "dont", "cancel", "stop", "leave it",
        "no leave it", "never mind", "nevermind", "not now", "forget it", "no don't", "no dont",
    };

    /// <summary>The message is a yes to whatever was offered, and nothing else.</summary>
    public static bool IsAffirmative(string message) => Affirmatives.Contains(Normalise(message));

    /// <summary>The message is a no to whatever was offered, and nothing else.</summary>
    public static bool IsNegative(string message) => Negatives.Contains(Normalise(message));

    /// <summary>Lower-cased, trimmed of whitespace and trailing punctuation, inner runs of
    /// whitespace collapsed — so "Yes, please!" and "yes please" are the same answer.</summary>
    private static string Normalise(string message)
    {
        var trimmed = message.Trim().TrimEnd('.', '!', '?', ',', ';', ':').Trim();
        return string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace(",", string.Empty);
    }

    private static string? Canonical(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim().ToLowerInvariant();
}
