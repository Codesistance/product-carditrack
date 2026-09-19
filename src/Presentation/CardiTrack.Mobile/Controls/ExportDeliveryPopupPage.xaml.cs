using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>What the caregiver chose to do with a finished export.</summary>
public enum ExportDelivery
{
    /// <summary>Keep it on this phone, where the OS files live.</summary>
    Save = 1,

    /// <summary>Hand it to another app through the system share sheet.</summary>
    Share = 2,
}

/// <summary>
/// Where a finished export should go, as two tiles rather than a system action list. Same shell,
/// card and glyph language as <see cref="ExportFormatPopupPage"/>, which asked the question
/// before it; dismissal returns null and the flow leaves the file in the cache, which the next
/// export sweeps.
/// </summary>
public partial class ExportDeliveryPopupPage : ContentPage
{
    // RunContinuationsAsynchronously so completing this from CloseAsync/OnDisappearing (both
    // already on the UI thread) doesn't run the awaiter's continuation synchronously in-line.
    private readonly TaskCompletionSource<ExportDelivery?> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _closing;

    /// <param name="fileName">Shown under the title — what to look for in Files, or in whatever
    /// app the caregiver sends it to.</param>
    /// <param name="saveHint">
    /// Where Save will put it on this platform, in the caregiver's words ("Downloads"), or
    /// <c>null</c> where the platform cannot keep a file anywhere they would find it again. The
    /// Save tile is then not shown at all: a choice that would quietly do nothing is worse than
    /// one choice honestly offered.
    /// </param>
    public ExportDeliveryPopupPage(string fileName, string? saveHint = null)
    {
        InitializeComponent();
        FileNameLabel.Text = fileName;

        if (saveHint is { Length: > 0 })
        {
            SaveHint.Text = $"Keep it in {saveHint}";
        }
        else
        {
            // Share takes the whole row rather than leaving a gap where the other tile was.
            SaveTile.IsVisible = false;
            Grid.SetColumn(ShareTile, 0);
            Grid.SetColumnSpan(ShareTile, 2);
        }

        // Without OverFullScreen, iOS removes the page underneath and the transparent
        // modal renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);
    }

    /// <summary>The chosen destination, or null when the popup was dismissed.</summary>
    public Task<ExportDelivery?> Closed => _closed.Task;

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
        if (!_closing && !Navigation.ModalStack.Contains(this))
            _closed.TrySetResult(null);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    private async void OnScrimTapped(object? sender, TappedEventArgs e) => await CloseAsync(null);

    private async void OnCloseTapped(object? sender, TappedEventArgs e) => await CloseAsync(null);

    private async void OnSaveTapped(object? sender, TappedEventArgs e) =>
        await CloseAsync(ExportDelivery.Save);

    private async void OnShareTapped(object? sender, TappedEventArgs e) =>
        await CloseAsync(ExportDelivery.Share);

    private async Task CloseAsync(ExportDelivery? result)
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
            _closed.TrySetResult(result);
        }
    }
}
