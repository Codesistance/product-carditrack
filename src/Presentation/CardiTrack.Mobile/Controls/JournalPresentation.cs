using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Journal;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// How a CardiJournal entry is presented wherever one appears — the period said the way a person
/// says it, and the urgency pill. Shared by the journal list and an entry's own detail page,
/// which must not disagree about either: the label a caregiver tapped on the list is a promise
/// about the screen it opens.
/// </summary>
internal static class JournalPresentation
{
    /// <inheritdoc cref="JournalLabels.PeriodLabel"/>
    /// <remarks>Reads the device clock for "today"; the logic itself lives in Core, where it is tested.</remarks>
    internal static string PeriodLabel(JournalCadence cadence, DateOnly localDate) =>
        JournalLabels.PeriodLabel(cadence, localDate, DateOnly.FromDateTime(DateTime.Now));

    /// <inheritdoc cref="JournalLabels.DayLabel"/>
    internal static string DayLabel(DateOnly date) =>
        JournalLabels.DayLabel(date, DateOnly.FromDateTime(DateTime.Now));

    /// <summary>
    /// The model's own read of how soon the family should act, as a pill — or nothing at all when
    /// it returned none, or one this app does not know. A pill is a claim about a member's health,
    /// so an unrecognised value shows no pill rather than a grey one implying the service judged
    /// the day and found it unremarkable.
    /// </summary>
    internal static Border? UrgencyPill(string? urgency)
    {
        var (text, colourKey) = urgency switch
        {
            "watch" => ("WATCH", "StatusGreen"),
            "check-in" => ("CHECK IN", "StatusYellow"),
            "concerning" => ("CONCERNING", "StatusOrange"),
            "act-now" => ("ACT NOW", "StatusRed"),
            _ => (null, null),
        };

        if (text is null || colourKey is null)
            return null;

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        return new Border
        {
            Style = (Style)resources["StatusPill"],
            BackgroundColor = (Color)resources[colourKey],
            VerticalOptions = LayoutOptions.Start,
            Content = new Label
            {
                Text = text,
                Style = (Style)resources["StatusPillText"],
                TextColor = (Color)resources["White"],
            },
        };
    }

    /// <summary>
    /// The list card's rail colour for an urgency, or null for none — the same left-edge
    /// colouring the alert tiles wear, which says how soon the day asked for attention without
    /// spending a pill on it. Same vocabulary as <see cref="UrgencyPill"/>, kept beside it so
    /// the two cannot drift.
    /// </summary>
    internal static Color? UrgencyRailColor(string? urgency)
    {
        var key = urgency switch
        {
            "watch" => "StatusGreen",
            "check-in" => "StatusYellow",
            "concerning" => "StatusOrange",
            "act-now" => "StatusRed",
            _ => null,
        };

        return key is null
            ? null
            : (Color)Microsoft.Maui.Controls.Application.Current!.Resources[key];
    }

    /// <summary>
    /// The wire word for an urgency filter choice a caregiver made by its display name, or null
    /// for the "any" choice. The inverse of <see cref="UrgencyPill"/>'s vocabulary, kept beside
    /// it so the two lists cannot drift.
    /// </summary>
    internal static string? UrgencyWireValue(string displayChoice) => displayChoice switch
    {
        "Watch" => "watch",
        "Check in" => "check-in",
        "Concerning" => "concerning",
        "Act now" => "act-now",
        _ => null,
    };
}
