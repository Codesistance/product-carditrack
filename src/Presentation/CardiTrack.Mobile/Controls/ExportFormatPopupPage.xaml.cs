using CardiTrack.Domain.Enums;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Which file an export should produce, as two tiles rather than two rows of text. Same shell
/// as the other popups; dismissal returns null, which the flow treats as "cancelled" exactly as
/// the chooser's Cancel row did.
/// </summary>
public partial class ExportFormatPopupPage : ContentPage
{
    // RunContinuationsAsynchronously so completing this from CloseAsync/OnDisappearing (both
    // already on the UI thread) doesn't run the awaiter's continuation synchronously in-line.
    private readonly TaskCompletionSource<ReportFormat?> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _closing;

    public ExportFormatPopupPage()
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent
        // modal renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);
    }

    /// <summary>The chosen format, or null when the popup was dismissed.</summary>
    public Task<ReportFormat?> Closed => _closed.Task;

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

    private async void OnPdfTapped(object? sender, TappedEventArgs e) =>
        await CloseAsync(ReportFormat.Pdf);

    private async void OnCsvTapped(object? sender, TappedEventArgs e) =>
        await CloseAsync(ReportFormat.Csv);

    private async Task CloseAsync(ReportFormat? result)
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
