using CardiTrack.Mobile.Core.Family;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The drawer behind the Family tab's header name (D-19): every family the caregiver is in, what
/// they are in each, and where the open alerts are — with the asks they are waiting on in the
/// same list, so their whole family situation is one thing to read.
/// </summary>
/// <remarks>
/// A switcher that only switches is bookkeeping. This one is worth opening because it says which
/// family needs attention, which is also why the rows are ordered by that rather than by name —
/// see <see cref="FamilyAlertState.OrderForDrawer"/>, which does the ordering so it can be tested.
/// </remarks>
public partial class FamilySwitcherPage : ContentPage
{
    /// <summary>How much of the screen the list may take before it scrolls.</summary>
    private const double ListShareOfPage = 0.45;

    private readonly TaskCompletionSource<FamilySwitcherChoice?> _result = new();
    private bool _closing;

    public FamilySwitcherPage(IReadOnlyList<FamilySwitcherRow> families, IReadOnlyList<string> waitingOn)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent modal
        // renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        foreach (var family in families)
            RowsHost.Add(FamilyRow(family));

        // A pending ask is a family-shaped thing that is not a family yet, and it belongs beside
        // the ones that are: this is the one list that answers "where do I stand".
        foreach (var name in waitingOn)
            RowsHost.Add(WaitingRow(name));

        if (families.Count == 0 && waitingOn.Count == 0)
        {
            RowsHost.Add(new Label
            {
                Text = "You're not in a family yet. Start one, or join the family somebody else set up.",
                Style = Named("Body2"),
                LineBreakMode = LineBreakMode.WordWrap,
            });
        }
    }

    public Task<FamilySwitcherChoice?> Result => _result.Task;

    private View FamilyRow(FamilySwitcherRow family)
    {
        var row = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 12,
            Padding = new Thickness(14, 12),
            MinimumHeightRequest = 60,
        };

        // The dot carries the family's worst open alert in the severity contract's own colours,
        // and green when there is nothing — a row that said nothing about its state would be the
        // bookkeeping this drawer exists not to be.
        var dot = new Border
        {
            WidthRequest = 12,
            HeightRequest = 12,
            StrokeThickness = 0,
            BackgroundColor = DotColour(family.Alerts),
            VerticalOptions = LayoutOptions.Center,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
        };
        row.Add(dot, 0, 0);

        var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
        text.Add(new Label
        {
            Text = family.Name,
            Style = Named("Body1SemiBoldDark"),
            LineBreakMode = LineBreakMode.TailTruncation,
        });
        text.Add(new Label
        {
            Text = $"{family.RoleLabel} · {AlertLine(family.Alerts)}",
            Style = Named("Body2"),
            LineBreakMode = LineBreakMode.TailTruncation,
        });
        row.Add(text, 1, 0);

        if (family.IsCurrent)
        {
            row.Add(new Image
            {
                Source = "icon_action_check.svg",
                WidthRequest = 20,
                HeightRequest = 20,
                VerticalOptions = LayoutOptions.Center,
            }, 2, 0);
        }

        var card = new Border
        {
            Style = Named("OutlinedCard"),
            Padding = new Thickness(0),
            Content = row,
        };
        // The current family is marked by the tick, not by a fill: a selected row in a different
        // colour reads as a different kind of row.
        SemanticProperties.SetDescription(card, family.IsCurrent
            ? $"{family.Name}, {family.RoleLabel}, {AlertLine(family.Alerts)}, currently showing"
            : $"{family.Name}, {family.RoleLabel}, {AlertLine(family.Alerts)}");

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await CloseAsync(new FamilySwitcherChoice.Chosen(family.OrganizationId));
        card.GestureRecognizers.Add(tap);
        return card;
    }

    /// <summary>
    /// An ask that has not been answered. It names the family it was sent to and nothing else —
    /// the caregiver typed that name's code, so it tells them nothing they did not already have.
    /// </summary>
    private View WaitingRow(string familyName)
    {
        var row = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            ColumnSpacing = 12,
            Padding = new Thickness(14, 12),
            MinimumHeightRequest = 60,
        };
        row.Add(new Border
        {
            WidthRequest = 12,
            HeightRequest = 12,
            StrokeThickness = 0,
            BackgroundColor = MetricStatus.Resource("StatusUnknown", Colors.Gray),
            VerticalOptions = LayoutOptions.Center,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
        }, 0, 0);

        var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
        text.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(familyName) ? "A family" : familyName,
            Style = Named("Body1SemiBoldDark"),
            LineBreakMode = LineBreakMode.TailTruncation,
        });
        text.Add(new Label { Text = "Waiting · their admin hasn't answered yet", Style = Named("Body2") });
        row.Add(text, 1, 0);

        return new Border { Style = Named("OutlinedCard"), Padding = new Thickness(0), Content = row };
    }

    private static string AlertLine(FamilyAlertSummary alerts) => alerts.IsQuiet
        ? "nothing open"
        : alerts.OpenCount == 1 ? "1 alert open" : $"{alerts.OpenCount} alerts open";

    private static Color DotColour(FamilyAlertSummary alerts) => MetricStatus.Resource(
        alerts.HighestSeverity?.ToLowerInvariant() switch
        {
            "red" => "StatusRed",
            "orange" => "StatusOrange",
            "yellow" => "StatusYellow",
            _ => "StatusGreen",
        },
        Colors.Gray);

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (height > 0)
            RowsScroll.MaximumHeightRequest = height * ListShareOfPage;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = Scrim.FadeToAsync(1, 140);
        _ = Card.TranslateToAsync(0, 0, 180, Easing.CubicOut);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Also fires on backgrounding and when another modal covers this one; only an external
        // dismissal — one that took the page off the stack without CloseAsync — resolves here.
        if (!_closing && !Navigation.ModalStack.Contains(this))
            _result.TrySetResult(null);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    private async void OnScrimTapped(object? sender, TappedEventArgs e) => await CloseAsync(null);

    private async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async void OnStartFamilyClicked(object? sender, EventArgs e) =>
        await CloseAsync(FamilySwitcherChoice.StartFamilyChoice);

    private async void OnJoinFamilyClicked(object? sender, EventArgs e) =>
        await CloseAsync(FamilySwitcherChoice.JoinFamilyChoice);

    private async Task CloseAsync(FamilySwitcherChoice? choice)
    {
        if (_closing)
            return;
        _closing = true;

        try
        {
            await Task.WhenAll(
                Scrim.FadeToAsync(0, 100),
                Card.TranslateToAsync(0, 40, 120, Easing.CubicIn));
            await Navigation.PopModalAsync(animated: false);
        }
        finally
        {
            _result.TrySetResult(choice);
        }
    }

    private static Style? Named(string key) =>
        Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var found) == true
            ? found as Style
            : null;
}
