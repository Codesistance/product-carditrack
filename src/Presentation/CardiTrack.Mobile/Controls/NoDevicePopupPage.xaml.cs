using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The no-device card (Figma M1-09 D) as a modal: illustration, "No device connected", the
/// member's line, and Connect. Completes <see cref="Result"/> with true when Connect was tapped,
/// false when dismissed — the page runs the connect flow, so this popup never navigates.
/// </summary>
/// <remarks>
/// Same shell as <see cref="WeatherPopupPage"/> and <see cref="AppPopupPage"/>. It used to be a
/// card on the dashboard itself, opened in place by the member card's no-device button; a modal
/// keeps the dashboard's layout still while it is read, and says plainly that this is a step to
/// take or put off rather than more of the page.
/// </remarks>
public partial class NoDevicePopupPage : ContentPage
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closing;

    public NoDevicePopupPage(string firstName)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent
        // modal renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        MessageLabel.Text = string.IsNullOrWhiteSpace(firstName)
            ? "Connect their device so CardiTrack can start watching over them"
            : $"Connect {firstName}'s device so CardiTrack can start watching over them";
    }

    /// <summary>True when Connect was tapped; false when dismissed, however that happened.</summary>
    public Task<bool> Result => _result.Task;

    /// <summary>Same width rule as the other popups; see <see cref="PopupCard"/>.</summary>
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
        // Also fires on app backgrounding and when another modal covers this one — in both
        // cases the page is still on the modal stack. Only resolve when it left without CloseAsync.
        if (!_closing && !Navigation.ModalStack.Contains(this))
            _result.TrySetResult(false);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(false);
        return true;
    }

    private async void OnScrimTapped(object? sender, TappedEventArgs e) => await CloseAsync(false);

    private async void OnNotNowClicked(object? sender, EventArgs e) => await CloseAsync(false);

    private async void OnConnectClicked(object? sender, EventArgs e) => await CloseAsync(true);

    private async Task CloseAsync(bool connect)
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
            _result.TrySetResult(connect);
        }
    }
}
