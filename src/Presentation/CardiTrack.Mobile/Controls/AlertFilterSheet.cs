using System.Globalization;
using CardiTrack.Mobile.Core.Alerts;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The Alerts list's filter, as a <see cref="FilterSheetPage"/>: whose alerts, which of them, how
/// serious, and since when.
/// </summary>
internal static class AlertFilterSheet
{
    /// <param name="current">The filter the list is showing now — the draft starts as it.</param>
    /// <param name="members">Whom the list can be narrowed to, in the order to offer them.</param>
    /// <param name="archived">
    /// Whether the list is the archive, where "which" does not apply: every alert there is
    /// resolved, so the section is left out rather than offered and ignored.
    /// </param>
    /// <param name="count">How many alerts a filter would show, or null when that is not known.</param>
    /// <returns>The sheet, and a read of the draft as it stands when the sheet closes.</returns>
    public static (FilterSheetPage Page, Func<AlertListFilter> Draft) Create(
        AlertListFilter current,
        IReadOnlyList<FilterMember> members,
        bool archived,
        Func<AlertListFilter, CancellationToken, Task<int?>> count)
    {
        var draft = current;

        // The member the list is already narrowed to is always offered, even when the member list
        // could not be read or no longer has them — otherwise the sheet could not show what is set.
        var offered = members.ToList();
        if (current.MemberId is { } setId && offered.All(m => m.Id != setId))
            offered.Insert(0, new FilterMember(setId, current.MemberName ?? AlertListFilter.UnnamedMemberLabel));

        var sections = new List<FilterSection>
        {
            new("Whose",
            [
                new("Everyone", null, () => draft.MemberId is null, () => draft = draft with { MemberId = null, MemberName = null }),
                .. offered.Select(m => new FilterChoice(
                    m.Name, null,
                    () => draft.MemberId == m.Id,
                    () => draft = draft with { MemberId = m.Id, MemberName = m.Name })),
            ]),
        };

        if (!archived)
        {
            sections.Add(new("Which",
                [.. Enum.GetValues<AlertStatusChoice>().Select(s => new FilterChoice(
                    AlertListFilter.StatusLabel(s), null,
                    () => draft.Status == s,
                    () => draft = draft with { Status = s }))]));
        }

        sections.Add(new("How serious",
            [.. Enum.GetValues<AlertSeverityChoice>().Select(s => new FilterChoice(
                AlertListFilter.SeverityLabel(s), SeverityColour(s),
                () => draft.Severity == s,
                () => draft = draft with { Severity = s }))]));

        sections.Add(new("When",
            [.. Enum.GetValues<AlertWindow>().Select(w => new FilterChoice(
                AlertListFilter.WindowLabel(w), null,
                () => draft.Window == w,
                () => draft = draft with { Window = w }))]));

        var page = new FilterSheetPage(
            "Filter alerts",
            sections,
            reset: () => draft = AlertListFilter.None,
            countLabel: async ct =>
            {
                var total = await count(draft, ct);
                return total switch
                {
                    null => null,
                    0 => "No alerts match",
                    1 => "Show 1 alert",
                    { } n => string.Create(CultureInfo.CurrentCulture, $"Show {n} alerts"),
                };
            },
            idleLabel: "Show alerts");

        return (page, () => draft);
    }

    /// <summary>The colour the alert cards give a severity; none for "Any".</summary>
    private static Color? SeverityColour(AlertSeverityChoice severity) => severity switch
    {
        AlertSeverityChoice.Critical => FilterSheetPage.Resource<Color>("StatusRed"),
        AlertSeverityChoice.Urgent => FilterSheetPage.Resource<Color>("StatusOrange"),
        AlertSeverityChoice.Notice => FilterSheetPage.Resource<Color>("StatusYellow"),
        AlertSeverityChoice.Info => FilterSheetPage.Resource<Color>("StatusGreen"),
        _ => null,
    };
}
