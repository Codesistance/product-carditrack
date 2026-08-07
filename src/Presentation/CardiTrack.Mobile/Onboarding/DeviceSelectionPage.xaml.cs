using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>M1-05: choose the wearable to connect. Fitbit only in MVP 1.</summary>
public partial class DeviceSelectionPage : ContentPage
{
    private readonly CardiMemberResponse _member;
    private bool _fitbitSelected = true;

    public DeviceSelectionPage(CardiMemberResponse member)
    {
        InitializeComponent();
        _member = member;
        TitleLabel.Text = $"What does {member.Name} wear?";
    }

    private void OnFitbitTapped(object? sender, EventArgs e)
    {
        _fitbitSelected = !_fitbitSelected;
        FitbitCard.Stroke = _fitbitSelected
            ? (Color)App.Current!.Resources["Primary"]
            : (Color)App.Current!.Resources["Divider"];
        FitbitCard.StrokeThickness = _fitbitSelected ? 2 : 1;
        FitbitCheck.IsVisible = _fitbitSelected;
        ContinueBtn.IsEnabled = _fitbitSelected;
    }

    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        if (!_fitbitSelected)
            return;
        await Navigation.PushAsync(new FitbitConnectionPage(_member));
    }

    private async void OnHelpTapped(object? sender, EventArgs e)
    {
        await ServiceHelper.GetRequiredService<IPopupService>().ShowInfoAsync(
            "More devices are on the way — Garmin lands next. Reach us at support@carditrack.com and we'll help you get set up.",
            "We can help");
    }
}
