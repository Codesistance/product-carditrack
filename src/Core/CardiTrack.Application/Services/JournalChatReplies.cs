using System.Globalization;
using System.Text;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// The journal rung's replies, assembled in code. No model writes any of these: the only
/// generated text a journal reply ever carries is a stored book, read back exactly as the Journal
/// tab shows it. Pure reply assembly, so it lives in Application like the other reply helpers
/// (docs/technical/member_chat_routing.md §13).
/// </summary>
public static class JournalChatReplies
{
    public static string BookName(DigestAudience audience) => audience switch
    {
        DigestAudience.Daybook => "Daybook",
        DigestAudience.Weekbook => "Weekbook",
        DigestAudience.Monthbook => "Monthbook",
        _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "Not a CardiJournal book."),
    };

    public static string PeriodNoun(DigestAudience audience) => audience switch
    {
        DigestAudience.Daybook => "day",
        DigestAudience.Weekbook => "week",
        DigestAudience.Monthbook => "month",
        _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "Not a CardiJournal book."),
    };

    /// <summary>The period a book covers, as a caregiver would say it.</summary>
    public static string PeriodLabel(DateOnly periodEnd, DigestAudience audience, DateOnly today)
    {
        switch (audience)
        {
            case DigestAudience.Daybook:
                return periodEnd == today.AddDays(-1) ? "yesterday" : Day(periodEnd);

            case DigestAudience.Weekbook:
                var start = JournalChatRequest.PeriodStart(periodEnd, audience);
                return $"the week of {Day(start)} to {Day(periodEnd)}";

            case DigestAudience.Monthbook:
                return periodEnd.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

            default:
                throw new ArgumentOutOfRangeException(nameof(audience), audience, "Not a CardiJournal book.");
        }
    }

    private static string Day(DateOnly date) => date.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>"Dad's" or "their" — the same fallback the other replies use, for the same
    /// reason: an invented relationship word reads worse than a pronoun.</summary>
    private static string Whose(string? firstName) =>
        string.IsNullOrWhiteSpace(firstName) ? "their" : $"{firstName}'s";

    // ── Asking the caregiver to say more ────────────────────────────────────

    public static string WhatToDo(string? firstName) =>
        $"I can show or list {Whose(firstName)} journal entries, delete one, or write one again from "
        + "that period's readings — which would you like?";

    public static string WhichBook(JournalChatAction action) => action switch
    {
        JournalChatAction.Show => "Which kind of entry would you like to see — a Daybook, a Weekbook or a Monthbook?",
        JournalChatAction.Discard => "Which kind of entry should I delete — a Daybook, a Weekbook or a Monthbook?",
        JournalChatAction.Rewrite => "Which kind of entry should I write again — a Daybook, a Weekbook or a Monthbook?",
        _ => "Which kind of entry — a Daybook, a Weekbook or a Monthbook?",
    };

    public static string WhichPeriod(JournalChatAction action, DigestAudience audience)
    {
        var verb = action switch
        {
            JournalChatAction.Discard => "delete",
            JournalChatAction.Rewrite => "write again",
            _ => "show",
        };
        var example = audience switch
        {
            DigestAudience.Daybook => "say the day, like \"Tuesday\" or \"12 September\"",
            DigestAudience.Weekbook => "say the week, like \"last week\" or \"the week of 7 September\"",
            _ => "say the month, like \"August\"",
        };
        return $"Which {BookName(audience)} should I {verb}? Just {example}.";
    }

    // ── Periods that cannot have a book ─────────────────────────────────────

    public static string NotFinished(DigestAudience audience) =>
        $"That {PeriodNoun(audience)} isn't over yet — a {BookName(audience)} is written once the "
        + $"{PeriodNoun(audience)} it describes has finished.";

    // ── Reading back ────────────────────────────────────────────────────────

    public static string NothingThere(DigestAudience audience, DateOnly? periodEnd, DateOnly today) =>
        periodEnd is { } end
            ? $"There's no {BookName(audience)} for {PeriodLabel(end, audience, today)}. If that {PeriodNoun(audience)} "
              + "had readings, you can ask me to write one."
            : $"There are no {BookName(audience)}s yet.";

    public static string Show(DigestEntry entry, DateOnly today)
    {
        var reply = new StringBuilder()
            .Append(BookName(entry.Audience)).Append(" for ").Append(PeriodLabel(entry.LocalDate, entry.Audience, today));
        if (!string.IsNullOrWhiteSpace(entry.Headline))
            reply.Append(" — ").Append(entry.Headline);
        reply.Append("\n\n").Append(entry.Text);
        if (!string.IsNullOrWhiteSpace(entry.Suggestion))
            reply.Append("\n\n").Append(entry.Suggestion);
        return reply.ToString();
    }

    public static string List(IReadOnlyList<DigestEntry> entries, DigestAudience audience, DateOnly today)
    {
        if (entries.Count == 0)
            return NothingThere(audience, null, today);

        var lines = new StringBuilder()
            .Append(entries.Count == 1 ? $"The one {BookName(audience)} so far:" : $"The most recent {BookName(audience)}s:");
        foreach (var entry in entries)
        {
            lines.Append("\n• ").Append(PeriodLabel(entry.LocalDate, audience, today));
            if (!string.IsNullOrWhiteSpace(entry.Headline))
                lines.Append(" — ").Append(entry.Headline);
        }

        lines.Append("\n\nAsk me to show one, delete one, or write one again.");
        return lines.ToString();
    }

    // ── Changing the journal ────────────────────────────────────────────────

    public static string OnlyPrimaryCaregiver(string? firstName) =>
        $"Only {Whose(firstName)} primary caregiver can change the journal. You can still ask me to show "
        + "or list any entry.";

    public static string ConfirmDiscard(JournalChatRequest request, DigestEntry existing, DateOnly today)
    {
        var book = BookName(request.Audience);
        var headline = string.IsNullOrWhiteSpace(existing.Headline) ? string.Empty : $" (\"{existing.Headline}\")";
        // Honest about the schedule: the due pass writes a book for a period with none, so a
        // deleted book for a period still inside its writing window comes back on the next pass.
        // Older periods stay deleted until someone asks.
        var noun = PeriodNoun(request.Audience);
        return $"This will delete the {book} for {PeriodLabel(request.PeriodEnd!.Value, request.Audience, today)}{headline}. "
            + $"You can ask me to write one again afterwards — and if that {noun} is still within the journal's "
            + "writing window, the schedule may write it again itself. Shall I go ahead?";
    }

    public static string ConfirmRewrite(JournalChatRequest request, DigestEntry? existing, DateOnly today)
    {
        var book = BookName(request.Audience);
        var period = PeriodLabel(request.PeriodEnd!.Value, request.Audience, today);
        var noun = PeriodNoun(request.Audience);
        return existing is null
            ? $"There's no {book} for {period} yet. Shall I write one from that {noun}'s readings? It usually takes a minute or two."
            : $"This will write the {book} for {period} again from that {noun}'s readings, replacing the one there now. "
              + "It usually takes a minute or two. Shall I go ahead?";
    }

    public static string LeftAsItIs() => "Okay — I've left the journal as it is.";

    /// <summary>A second change asked for while another is still waiting on its yes or no.</summary>
    public static string AnotherOfferWaiting() =>
        "There's already a change waiting for your yes or no — answer that first, then ask me again.";

    /// <summary>A yes to an offer that is no longer there: already carried out by another request,
    /// or replaced by a newer one. Named both ways because the chat cannot tell which from a missed
    /// claim, and guessing wrong would tell a caregiver a book was rewritten when it was not.</summary>
    public static string OfferGone() =>
        "That offer has already been answered or replaced, so I haven't changed anything. Ask again if you still want it.";

    public static string Discarded(JournalChatRequest request, DateOnly today) =>
        $"Done — the {BookName(request.Audience)} for {PeriodLabel(request.PeriodEnd!.Value, request.Audience, today)} has been deleted.";

    public static string NothingToDiscard(JournalChatRequest request, DateOnly today) =>
        $"There's no {BookName(request.Audience)} for {PeriodLabel(request.PeriodEnd!.Value, request.Audience, today)} to delete.";

    public static string Written(JournalRewriteResult result, DateOnly today)
    {
        var entry = result.Entry ?? throw new ArgumentException("A written result carries its entry.", nameof(result));
        var lead = result.ReplacedAnEarlierBook ? "Done — here's the new " : "Done — I've written the ";
        return lead + Show(entry, today);
    }

    public static string NotWritten(
        JournalRewriteResult result, JournalChatRequest request, bool hadABook, string? firstName, DateOnly today)
    {
        var book = BookName(request.Audience);
        var period = PeriodLabel(request.PeriodEnd!.Value, request.Audience, today);
        var noun = PeriodNoun(request.Audience);

        return result.Outcome switch
        {
            // One outcome for two states — paused, or no longer active — so the sentence names both
            // rather than guessing at the one the caregiver would recognise.
            JournalRewriteOutcome.MemberUnavailable =>
                $"I can't write to {Whose(firstName)} journal just now — their monitoring is paused, or their "
                + "account is no longer active.",
            JournalRewriteOutcome.PeriodNotFinished => NotFinished(request.Audience),
            JournalRewriteOutcome.NoReadings when request.Audience == DigestAudience.Daybook =>
                $"There are no readings for {period}, so there's nothing to write a {book} from.",
            JournalRewriteOutcome.NoReadings =>
                $"Only {result.DaysWithData} of the days in {period} carried readings — a {book} needs at least "
                + $"{result.DaysNeeded}, so there isn't enough of that {noun} to account for.",
            JournalRewriteOutcome.Discarded when hadABook =>
                $"The new {book} didn't pass the checks every entry has to, so I've left the existing one in place. "
                + "You're welcome to ask me to try again.",
            JournalRewriteOutcome.Discarded =>
                $"The {book} I wrote didn't pass the checks every entry has to, so there's still none for {period}. "
                + "You're welcome to ask me to try again.",
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "A written result is not a failure."),
        };
    }
}
