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
        MessageLabel.Text = message;
        ConfirmBtn.Text = confirmText;
        if (_isConfirmation)
        {
            CancelBtn.Text = cancelText;
            CancelBtn.IsVisible = true;
            CancelColumn.Width = GridLength.Star;
        }
    }

    /// <summary>Completes when the popup is dismissed; true unless Cancel/back dismissed it.</summary>
    public Task<bool> Result => _result.Task;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = Scrim.FadeToAsync(1, 140);
        _ = Card.ScaleToAsync(1, 140, Easing.CubicOut);
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

        await Task.WhenAll(
            Scrim.FadeToAsync(0, 100),
            Card.ScaleToAsync(0.92, 100, Easing.CubicIn));
        await Navigation.PopModalAsync(animated: false);
        _result.TrySetResult(confirmed);
    }
}
