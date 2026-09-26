using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The applied-filter strip's pieces, shared by every list with a <see cref="FilterSheetPage"/> —
/// the Alerts list and the CardiJournal — so a narrowed list reads, and is undone, the same way
/// wherever it is.
/// </summary>
internal static class FilterStripPill
{
    /// <summary>One applied part: its words and a ✕, the whole pill a tap that removes it.</summary>
    public static View Create(string label, Action remove)
    {
        var pill = new Border
        {
            Padding = new Thickness(12, 6, 10, 6),
            StrokeThickness = 0,
            BackgroundColor = FilterSheetPage.Resource<Color>("SelectedOptionBackground"),
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Content = new HorizontalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new Label
                    {
                        Text = label,
                        FontFamily = "QuicksandSemiBold",
                        FontSize = 13,
                        TextColor = FilterSheetPage.Resource<Color>("PrimaryDark"),
                        VerticalTextAlignment = TextAlignment.Center,
                        LineBreakMode = LineBreakMode.TailTruncation,
                        MaximumWidthRequest = 160,
                    },
                    new Label
                    {
                        Text = "✕",
                        FontFamily = "QuicksandSemiBold",
                        FontSize = 11,
                        TextColor = FilterSheetPage.Resource<Color>("PrimaryDark"),
                        VerticalTextAlignment = TextAlignment.Center,
                    },
                },
            },
        };
        SemanticProperties.SetDescription(pill, $"{label} filter");
        SemanticProperties.SetHint(pill, "Double tap to remove");
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => remove();
        pill.GestureRecognizers.Add(tap);
        return pill;
    }

    /// <summary>The strip's last item while more than one part is on: widens every part at once.</summary>
    public static View ClearAll(Action clear)
    {
        var link = new Label
        {
            Text = "Clear all",
            Style = FilterSheetPage.Resource<Style>("SectionLink"),
            VerticalTextAlignment = TextAlignment.Center,
            Padding = new Thickness(4, 6),
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => clear();
        link.GestureRecognizers.Add(tap);
        return link;
    }
}
