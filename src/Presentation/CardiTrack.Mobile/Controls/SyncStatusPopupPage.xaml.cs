using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// What the freshness dot on a CardiMember card means: when their data last arrived, in both
/// the forms a caregiver reads, and the pipeline's own word for the state. Same shell as
/// <see cref="AppPopupPage"/>, <see cref="AppChooserPage"/> and <see cref="WeatherPopupPage"/>.
/// </summary>
public partial class SyncStatusPopupPage : ContentPage
{
    // RunContinuationsAsynchronously so completing this from CloseAsync/OnDisappearing (both
    // already on the UI thread) doesn't run the awaiter's continuation synchronously in-line.
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closing;

    /// <param name="tier">The API's freshness word — red, amber, blue or green.</param>
    /// <param name="stateMessage">The pipeline's own description of that tier.</param>
    /// <param name="lastSyncedUtc">When data last arrived, or null if it never has.</param>
    public SyncStatusPopupPage(string? tier, string? stateMessage, DateTime? lastSyncedUtc)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent
        // modal renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        var accent = FreshnessPalette.ColorFor(tier);
        // The disc and the state row are the dot's colour at the weight a background can
        // carry; the text over them stays the body colour, which holds its contrast on a pale
        // wash of any of the four tiers — amber over amber would not.
        var wash = accent.WithAlpha(0.14f);
        Badge.BackgroundColor = wash;
        BadgeDot.Fill = accent;
        StateRow.BackgroundColor = wash;

        if (lastSyncedUtc is { } lastSynced)
        {
            AgeLabel.Text = RelativeTime.Format(lastSynced);
            ClockLabel.Text = DateTime.SpecifyKind(lastSynced, DateTimeKind.Utc)
                .ToLocalTime()
                .ToString("MMM d, h:mm tt");
        }
        else
        {
            AgeLabel.Text = "Not yet";
            ClockLabel.Text = "No data has arrived from this CardiMember.";
        }

        StateLabel.Text = stateMessage ?? string.Empty;
        StateRow.IsVisible = !string.IsNullOrWhiteSpace(stateMessage);

        // One node says the whole thing: a reader should not have to piece the state together
        // from a decorative dot, a heading and a row.
        SemanticProperties.SetDescription(
            Card, $"Sync status. {StateLabel.Text}. Last synced {AgeLabel.Text}, {ClockLabel.Text}");
    }

    /// <summary>Completes when the popup is dismissed, however that happened.</summary>
    public Task Closed => _closed.Task;

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
            _closed.TrySetResult();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }

    private async void OnScrimTapped(object? sender, TappedEventArgs e) => await CloseAsync();

    private async void OnCloseTapped(object? sender, EventArgs e) => await CloseAsync();

    private async Task CloseAsync()
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
            _closed.TrySetResult();
        }
    }
}
