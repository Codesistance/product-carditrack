using System.Globalization;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Journal;

namespace CardiTrack.Mobile.Controls;

/// <summary>What the CardiJournal's filter sheet was closed on: whose journal, and how it is narrowed.</summary>
public sealed record JournalFilterChoice(FilterMember Member, JournalListFilter Filter);

/// <summary>
/// The CardiJournal list's filter, as a <see cref="FilterSheetPage"/> — the Alerts list's sheet
/// with the journal's questions: whose journal, how soon it asked you to act, and since when.
/// </summary>
/// <remarks>
/// "Whose" differs from the Alerts sheet's on purpose. The journal is always one member's, so there
/// is no "Everyone", and the section is left out while the account has only one member — a
/// question with one answer is a control with nothing to do.
/// </remarks>
internal static class JournalFilterSheet
{
    /// <param name="current">What the list is showing now — the draft starts as it.</param>
    /// <param name="members">Every member on the account, in the order to offer them.</param>
    /// <param name="cadence">The book open on the page, so the button counts in its own noun.</param>
    /// <param name="pageLimit">
    /// How many entries the list loads. The journal endpoint has no total, so a count that reaches
    /// the page's own size says "the latest N" rather than claiming that is all there is.
    /// </param>
    /// <param name="count">How many entries a choice would list, or null when that is not known.</param>
    /// <returns>The sheet, and a read of the draft as it stands when the sheet closes.</returns>
    public static (FilterSheetPage Page, Func<JournalFilterChoice> Draft) Create(
        JournalFilterChoice current,
        IReadOnlyList<FilterMember> members,
        JournalCadence cadence,
        int pageLimit,
        Func<JournalFilterChoice, CancellationToken, Task<int?>> count)
    {
        var draft = current;
        var sections = new List<FilterSection>();

        // The member on screen is always offered, so the sheet can show what is set even when the
        // list it was handed has lost them.
        var offered = members.ToList();
        if (offered.All(m => m.Id != current.Member.Id))
            offered.Insert(0, current.Member);

        if (offered.Count > 1)
        {
            sections.Add(new("Whose",
                [.. offered.Select(m => new FilterChoice(
                    m.Name, null,
                    () => draft.Member.Id == m.Id,
                    () => draft = draft with { Member = m }))]));
        }

        sections.Add(new("How soon",
            [.. Enum.GetValues<JournalUrgencyChoice>().Select(u => new FilterChoice(
                JournalListFilter.UrgencyLabel(u),
                JournalPresentation.UrgencyRailColor(JournalListFilter.UrgencyWireValue(u)),
                () => draft.Filter.Urgency == u,
                () => draft = draft with { Filter = draft.Filter with { Urgency = u } }))]));

        sections.Add(new("When",
            [.. Enum.GetValues<JournalWindow>().Select(w => new FilterChoice(
                JournalListFilter.WindowLabel(w), null,
                () => draft.Filter.Window == w,
                () => draft = draft with { Filter = draft.Filter with { Window = w } }))]));

        var noun = cadence.EntryName();
        var page = new FilterSheetPage(
            "Filter the journal",
            sections,
            // Reset widens the filter but keeps whose journal it is: there is no "nobody" to reset
            // the member to, and the member the page opened on is not a narrowing.
            reset: () => draft = draft with { Filter = JournalListFilter.None },
            countLabel: async ct =>
            {
                var total = await count(draft, ct);
                return total switch
                {
                    null => null,
                    0 => $"No {noun}s match",
                    1 => $"Show 1 {noun}",
                    { } n when n >= pageLimit => string.Create(CultureInfo.CurrentCulture, $"Show latest {pageLimit}"),
                    { } n => string.Create(CultureInfo.CurrentCulture, $"Show {n} {noun}s"),
                };
            },
            idleLabel: $"Show {noun}s");

        return (page, () => draft);
    }
}
