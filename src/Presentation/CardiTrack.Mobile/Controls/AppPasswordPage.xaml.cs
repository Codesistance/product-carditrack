using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Password step-up for export consent. Completes with the typed password, or
/// null when cancelled. The page never sends the password anywhere — the caller
/// verifies it against Auth0.
/// </summary>
public partial class AppPasswordPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _result = new();
    private bool _closing;

    public AppPasswordPage(string title, string message)
    {
        InitializeComponent();
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);
        TitleLabel.Text = title;
        MessageLabel.Text = message;
    }

    public Task<string?> Result => _result.Task;

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
        PasswordEntry.Focus();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_closing && !Navigation.ModalStack.Contains(this))
            _result.TrySetResult(null);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    private async void OnScrimTapped(object? sender, TappedEventArgs e) =>
        await CloseAsync(null);

    private async void OnCancelClicked(object? sender, EventArgs e) =>
        await CloseAsync(null);

    private async void OnConfirmClicked(object? sender, EventArgs e)
    {
        var password = PasswordEntry.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
            return;
        await CloseAsync(password);
    }

    private async Task CloseAsync(string? password)
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
            _result.TrySetResult(password);
        }
    }
}
