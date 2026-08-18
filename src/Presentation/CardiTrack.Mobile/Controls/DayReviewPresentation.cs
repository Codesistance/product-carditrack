using System.Globalization;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// How a day review is presented wherever one appears — the day said the way a person says it,
/// and the urgency pill. Shared by the Summaries list and the review's own detail page, which
/// must not disagree about either: the pill a caregiver tapped on the list is a promise about
/// the screen it opens.
/// </summary>
internal static class DayReviewPresentation
{
    /// <summary>
    /// "Yesterday" for the day just gone, the weekday for the rest of the week, and the date
    /// beyond that — the way someone talks about their own week rather than a row of dates.
    /// </summary>
    /// <remarks>
    /// Against the device's today, not the member's: this is the reader's label for when they are
    /// reading, and a caregiver in another timezone reading "Yesterday" about the day their own
    /// yesterday was is the sentence they would have said themselves.
    /// </remarks>
    internal static string DayLabel(DateOnly date)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var age = today.DayNumber - date.DayNumber;

        return age switch
        {
            0 => "Today",
            1 => "Yesterday",
            > 1 and < 7 => date.ToString("dddd", CultureInfo.CurrentCulture),
            _ => date.ToString("d MMMM", CultureInfo.CurrentCulture),
        };
    }

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
