using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>
/// M1-07: connection confirmed. Pulls a first data preview from the dashboard endpoint;
/// while values are missing the card stays in its syncing/partial state (M1-07 A/C).
/// </summary>
public partial class ConnectionSuccessPage : ContentPage
{
    private readonly ICardiTrackApiClient _api;
    private readonly CardiMemberResponse _member;

    public ConnectionSuccessPage(CardiMemberResponse member, DeviceResponse device)
    {
        InitializeComponent();
        _api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();
        _member = member;
        ConnectedLabel.Text = $"{member.Name}'s {device.DisplayName} is now connected";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadPreviewAsync();
    }

    private async Task LoadPreviewAsync()
    {
        try
        {
            var dashboard = await _api.GetDashboardAsync(_member.Id);

            var steps = dashboard.Metrics?.Steps.Value;
            var heartRate = dashboard.Metrics?.RestingHeartRate.Value;

            if (steps is not null)
                StepsValue.Text = $"{steps:N0}";
            if (heartRate is not null)
                HeartRateValue.Text = $"{heartRate} bpm";
            if (dashboard.LastSyncedAt is { } synced)
                SyncedValue.Text = FormatSynced(synced);

            var syncedAll = steps is not null && heartRate is not null;
            SyncBadgeLabel.Text = syncedAll ? "SYNCED" : "PARTIAL SYNC";
        }
        catch (ApiException)
        {
            // First sync hasn't landed yet — the card keeps its M1-07 A syncing state.
        }
    }

    private static string FormatSynced(DateTime syncedUtc)
    {
        var elapsed = DateTime.UtcNow - syncedUtc.ToUniversalTime();
        return elapsed < TimeSpan.FromMinutes(2) ? "Just now"
            : elapsed < TimeSpan.FromHours(1) ? $"{(int)elapsed.TotalMinutes} min ago"
            : $"{(int)elapsed.TotalHours}h ago";
    }

    private async void OnAddAnotherClicked(object? sender, EventArgs e)
    {
        // Back to M1-05 for a second device.
        await Navigation.PushAsync(new DeviceSelectionPage(_member));
    }

    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new BaselineLearningPage(_member));
    }
}
