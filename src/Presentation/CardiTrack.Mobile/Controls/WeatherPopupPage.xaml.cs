using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The detail behind the dashboard/detail weather chip: temperature, condition, humidity and
/// air quality from the member's last exercise session, with when it was taken so a reader
/// does not mistake it for live weather. Same shell as <see cref="AppPopupPage"/> and
/// <see cref="AppChooserPage"/> — see <see cref="WeatherSnapshotResponse"/> for the data.
/// </summary>
public partial class WeatherPopupPage : ContentPage
{
    // RunContinuationsAsynchronously so completing this from CloseAsync/OnDisappearing (both
    // already on the UI thread) doesn't run the awaiter's continuation synchronously in-line.
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closing;

    public WeatherPopupPage(WeatherSnapshotResponse weather)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent
        // modal renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        GlyphLabel.Text = WeatherGlyph.For(weather.Condition);
        TemperatureLabel.Text = weather.TemperatureCelsius is { } temperature
            ? $"{temperature:F0}°C"
            : "No temperature reading";
        ConditionLabel.Text = weather.Condition ?? string.Empty;
        ConditionLabel.IsVisible = !string.IsNullOrWhiteSpace(weather.Condition);

        // Independently optional, same as the AI prompt context that reads this entity — the
        // enrichment pass can succeed for one field and fail for the rest.
        if (weather.HumidityPercent is { } humidity)
            DetailRows.Add(DetailRow($"Humidity {humidity}%"));
        if (weather.AirQualityCategory is { } category && !string.IsNullOrWhiteSpace(category))
            DetailRows.Add(DetailRow($"Air quality: {category}"));

        AsOfLabel.Text = $"From their last exercise session, {RelativeTime.Format(weather.AsOfUtc)}";
    }

    private static Label DetailRow(string text) => new()
    {
        Text = text,
        Style = (Style)App.Current!.Resources["Body2Dark"],
        HorizontalTextAlignment = TextAlignment.Center,
    };

    /// <summary>Completes when the popup is dismissed, however that happened.</summary>
    public Task Closed => _closed.Task;

    /// <summary>Same width rule as the other two popups; see <see cref="PopupCard"/>.</summary>
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

    private async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync();

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
