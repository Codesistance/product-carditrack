using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Devices;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>
/// M1-05: choose the wearable to connect. Fitbit and Google Pixel Watch are selectable — both
/// sync through the Google Health API, so they share the same connection flow.
/// </summary>
public partial class DeviceSelectionPage : ContentPage
{
    private readonly WizardContext _ctx;
    private readonly CardiMemberResponse _member;
    private ConnectableDevice? _selected = ConnectableDevice.Fitbit;

    public DeviceSelectionPage(WizardContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;
        _member = ctx.RequireMember();
        TitleLabel.Text = $"What does {_member.Name} wear?";

        if (ctx.Origin == WizardOrigin.Modal)
        {
            // Mid-flow entry: the onboarding "Step N of 4" story doesn't apply.
            Header.Step = string.Empty;
            Header.Progress = 0;
        }
    }

    // The WizardHeader fallback resolves the window root's navigation, which is not
    // the wizard's own stack when launched modally — handle back explicitly.
    private async void OnBackRequested(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
            await Navigation.PopAsync();
        else
            await _ctx.CancelAsync(this);
    }

    private void OnFitbitTapped(object? sender, EventArgs e) =>
        Select(_selected == ConnectableDevice.Fitbit ? null : ConnectableDevice.Fitbit);

    private void OnPixelTapped(object? sender, EventArgs e) =>
        Select(_selected == ConnectableDevice.PixelWatch ? null : ConnectableDevice.PixelWatch);

    /// <summary>Single-select across the supported cards; re-tapping the selected one clears it.</summary>
    private void Select(ConnectableDevice? device)
    {
        _selected = device;
        StyleCard(FitbitCard, FitbitCheck, _selected == ConnectableDevice.Fitbit);
        StyleCard(PixelCard, PixelCheck, _selected == ConnectableDevice.PixelWatch);
        ContinueBtn.IsEnabled = _selected is not null;
        ContinueBtn.Text = _selected is null
            ? "Continue"
            : $"Continue with {_selected.DisplayName}";
    }

    private static void StyleCard(Border card, Border check, bool selected)
    {
        card.Stroke = selected
            ? (Color)App.Current!.Resources["Primary"]
            : (Color)App.Current!.Resources["Divider"];
        card.StrokeThickness = selected ? 2 : 1;
        check.IsVisible = selected;
    }

    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        if (_selected is null)
            return;
        await Navigation.PushAsync(new DeviceConnectionPage(_ctx, _selected));
    }

    private async void OnHelpTapped(object? sender, EventArgs e)
    {
        await ServiceHelper.GetRequiredService<IPopupService>().ShowInfoAsync(
            "More devices are on the way — Garmin lands next. Reach us at support@carditrack.com and we'll help you get set up.",
            "We can help");
    }
}
