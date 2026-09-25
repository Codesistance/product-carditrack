using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The list a <see cref="ChoiceField"/> opens: every option as a row, the current one ticked, in
/// the popup shell <see cref="AppPopupPage"/> and <see cref="AppChooserPage"/> share. Completes
/// <see cref="Result"/> with the tapped row's index, or null when cancelled or dismissed.
/// </summary>
/// <remarks>
/// Its own page rather than a mode of the chooser, because the two answer different questions.
/// The chooser asks one with no current answer — "pause for how long?" — and pill buttons suit
/// that. A field has an answer already, and a caregiver changing it needs to see which row is
/// set before they touch another; a tick on a row says that, a row of identical pills cannot.
/// Rows are 48pt so a thumb lands, and the list scrolls, because the alarm builder's reading list
/// is longer than a card.
/// </remarks>
public partial class ChoiceSheetPage : ContentPage
{
    private const double RowHeight = 48;

    /// <summary>How much of the page the list may take before it scrolls.</summary>
    private const double ListShareOfPage = 0.5;

    private readonly TaskCompletionSource<int?> _result = new();
    private readonly View? _selectedRow;
    private bool _closing;

    /// <param name="hint">
    /// One line under the title for a question the options cannot answer alone. Null for none.
    /// </param>
    public ChoiceSheetPage(string title, IReadOnlyList<string> options, int selectedIndex, string? hint = null)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent
        // modal renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        TitleLabel.Text = title;
        TitleLabel.IsVisible = !string.IsNullOrEmpty(title);
        HintLabel.Text = hint;
        HintLabel.IsVisible = !string.IsNullOrWhiteSpace(hint);

        var primary = MetricStatus.Resource("Primary", Colors.Blue);
        var ink = MetricStatus.Resource("HeadingText", Colors.Black);
        var rowFill = MetricStatus.Resource("InputBackground", Colors.WhiteSmoke);
        var selectedFill = MetricStatus.Resource("SelectedOptionBackground", Colors.AliceBlue);
        var radioRing = MetricStatus.Resource("MutedText", Colors.Gray);

        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var selected = i == selectedIndex;

            // Each option a filled row with a radio mark, rather than a line of text between
            // dividers: a row reads as something to tap, and the empty ring beside every option
            // says "pick one" before anybody has. The row that is set is tinted and ringed in
            // Primary with a filled check, so the current answer is visible from across the
            // sheet — the bare tick on the old list was easy to miss beside four plain rows.
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
                ColumnSpacing = 12,
                MinimumHeightRequest = RowHeight,
                Padding = new Thickness(14, 8),
            };

            var label = new Label
            {
                Text = options[i],
                FontFamily = selected ? "QuicksandSemiBold" : "QuicksandMedium",
                FontSize = 15,
                TextColor = selected ? primary : ink,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.WordWrap,
            };
            Grid.SetColumn(label, 0);
            row.Add(label);

            View mark = selected
                ? new Border
                {
                    WidthRequest = 20,
                    HeightRequest = 20,
                    StrokeThickness = 0,
                    BackgroundColor = primary,
                    StrokeShape = new Ellipse(),
                    VerticalOptions = LayoutOptions.Center,
                    Content = new Image
                    {
                        Source = "icon_check_white.svg",
                        WidthRequest = 12,
                        HeightRequest = 12,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                    },
                }
                : new Border
                {
                    WidthRequest = 20,
                    HeightRequest = 20,
                    Stroke = radioRing,
                    StrokeThickness = 2,
                    BackgroundColor = Colors.Transparent,
                    StrokeShape = new Ellipse(),
                    VerticalOptions = LayoutOptions.Center,
                };
            Grid.SetColumn(mark, 1);
            row.Add(mark);

            var card = new Border
            {
                BackgroundColor = selected ? selectedFill : rowFill,
                Stroke = selected ? primary : Colors.Transparent,
                StrokeThickness = selected ? 1.5 : 0,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Content = row,
            };

            SemanticProperties.SetDescription(card, selected ? $"{options[i]}, selected" : options[i]);

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await CloseAsync(index);
            card.GestureRecognizers.Add(tap);

            OptionsHost.Add(card);

            if (selected)
                _selectedRow = card;
        }
    }

    /// <summary>Completes with the tapped row's index, or null if cancelled or dismissed.</summary>
    public Task<int?> Result => _result.Task;

    /// <summary>
    /// Same width rule as the popup this shares its shell with — see <see cref="PopupCard"/> — and
    /// a hard ceiling on the list at half the page, so a long one scrolls inside the card rather
    /// than growing it off the screen. Half, whatever the screen: on one too short for three rows
    /// in half its height the list shows fewer and scrolls, which still leaves the title and the
    /// Cancel row where a thumb can reach them.
    /// </summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        PopupCard.Fit(Card, width);

        if (height > 0)
            OptionsScroll.MaximumHeightRequest = height * ListShareOfPage;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = Scrim.FadeToAsync(1, 140);
        _ = Card.ScaleToAsync(1, 140, Easing.CubicOut);

        // A long list opens on the row that is set, not on the top — that row is the one the
        // caregiver is here to read.
        if (_selectedRow is not null)
            _ = OptionsScroll.ScrollToAsync(_selectedRow, ScrollToPosition.MakeVisible, animated: false);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Also fires on app backgrounding and when another modal covers this one — in both
        // cases the page is still on the modal stack. Only resolve when the page left the
        // stack without CloseAsync (external dismissal, e.g. a root swap).
        if (!_closing && !Navigation.ModalStack.Contains(this))
            _result.TrySetResult(null);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    /// <summary>Tapping away leaves the field as it was — the same out the chooser gives.</summary>
    private async void OnScrimTapped(object? sender, TappedEventArgs e) => await CloseAsync(null);

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async Task CloseAsync(int? choice)
    {
        if (_closing)
            return;
        _closing = true;

        try
        {
            await Task.WhenAll(
                Scrim.FadeToAsync(0, 100),
                Card.ScaleToAsync(0.92, 100, Easing.CubicIn));
            await Navigation.PopModalAsync(animated: false);
        }
        finally
        {
            _result.TrySetResult(choice);
        }
    }
}
