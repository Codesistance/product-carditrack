using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Transparent modal page behind <see cref="PopupService"/>. Pushed onto the modal
/// stack so it sits above whatever root is active (Shell, NavigationPage, or a bare
/// page); dismissal resolves <see cref="Result"/>.
/// </summary>
public partial class AppPopupPage : ContentPage
{
    /// <summary>Caps runaway messages (e.g. raw error bodies) so the popup stays readable.</summary>
    private const int MaxMessageLength = 600;

    /// <summary>Gap between Cancel and Confirm, applied only when there are two buttons.</summary>
    private const double ButtonGap = 12;

    private readonly TaskCompletionSource<bool> _result = new();
    private readonly bool _isConfirmation;
    private bool _closing;

    public AppPopupPage(PopupSeverity severity, string title, string message, string confirmText, string? cancelText)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent
        // modal renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        _isConfirmation = cancelText is not null;

        var (glyph, colorKey) = severity switch
        {
            PopupSeverity.Warning => ("!", "StatusOrange"),
            PopupSeverity.Error => ("!", "ErrorRed"),
            _ => ("i", "Primary"),
        };
        var accent = (Color)App.Current!.Resources[colorKey];
        IconBadge.BackgroundColor = accent.WithAlpha(0.14f);
        IconLabel.Text = glyph;
        IconLabel.TextColor = accent;

        TitleLabel.Text = title;
        MessageLabel.Text = Truncate(message);

        ConfirmBtn.Text = confirmText;
        if (_isConfirmation)
        {
            CancelBtn.Text = cancelText;
            CancelBtn.IsVisible = true;
            CancelColumn.Width = GridLength.Star;
            // Only now is there a second button for the gutter to separate — see the row's
            // comment in XAML for what an always-on gutter did to the single-button case.
            ButtonRow.ColumnSpacing = ButtonGap;
        }
    }

    /// <summary>Completes when the popup is dismissed; true unless Cancel/back dismissed it.</summary>
    public Task<bool> Result => _result.Task;

    private static string Truncate(string message)
    {
        if (message.Length <= MaxMessageLength)
            return message;

        var cut = message.LastIndexOf(' ', MaxMessageLength);
        return message[..(cut > 0 ? cut : MaxMessageLength)].TrimEnd() + "…";
    }

    /// <summary>
    /// The card is given a width outright, on every size pass, rather than a maximum — see
    /// <see cref="PopupCard"/> for what a maximum did to a long message.
    /// </summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        PopupCard.Fit(Card, width);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = Scrim.FadeToAsync(1, 140);
        _ = Card.ScaleToAsync(1, 140, Easing.CubicOut);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Also fires on app backgrounding and when another modal covers this one —
        // in both cases the page is still on the modal stack. Only resolve when the
        // page left the stack without CloseAsync (external dismissal, e.g. a root swap).
        if (!_closing && !Navigation.ModalStack.Contains(this))
            _result.TrySetResult(false);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(false);
        return true;
    }

    private async void OnScrimTapped(object? sender, TappedEventArgs e)
    {
        // Confirmations require an explicit choice; the rest dismiss on scrim tap.
        if (!_isConfirmation)
            await CloseAsync(true);
    }

    private async void OnConfirmClicked(object? sender, EventArgs e) => await CloseAsync(true);

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(false);

    private async Task CloseAsync(bool confirmed)
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
            _result.TrySetResult(confirmed);
        }
    }
}
