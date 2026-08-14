using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class FamilyPage : ContentPage
{
    public FamilyPage()
    {
        InitializeComponent();
    }

    /// <summary>Family is a tab root, same as Alerts and Settings — the arrow falls back to the
    /// dashboard when there is no history of its own to pop.</summary>
    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.DashboardRoute);
}
