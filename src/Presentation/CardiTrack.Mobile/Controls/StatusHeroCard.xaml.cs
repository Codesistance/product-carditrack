using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

public partial class StatusHeroCard : ContentView
{
    public event EventHandler? SyncRequested;

    /// <summary>Raised when the card body is tapped — the dashboard's route into M1-13.</summary>
    public event EventHandler? MemberTapped;

    public StatusHeroCard()
    {
        InitializeComponent();
    }

    public void Apply(DashboardResponse data)
    {
        var firstName = NameFormatting.FirstName(data.Name);
        NameLabel.Text = $"{data.Name}, {data.Age}";
        InitialsLabel.Text = NameFormatting.Initials(data.Name);
        LastSyncedLabel.Text = data.LastSyncedAt is { } synced
            ? $"Updated {RelativeTime.Format(synced)}"
            : "Not synced yet";

        var (brush, icon, statusText) = data.HealthStatus switch
        {
            "green" => ("HeroGreenBrush", "icon_status_check.svg", $"{firstName} is doing well"),
            "yellow" => ("HeroYellowBrush", "icon_status_warning.svg", "Something looks a little different"),
            "orange" => ("HeroOrangeBrush", "icon_status_urgent.svg", "You should check in"),
            "red" => ("HeroRedBrush", "icon_status_critical.svg", $"Reach out to {firstName} now"),
            // Paused is not a health reading — never dress it up as one.
            "paused" => ("HeroPausedBrush", "icon_status_paused.svg", $"Monitoring is paused for {firstName}"),
            _ => ("HeroUnknownBrush", "icon_status_check.svg", "Getting to know their routine"),
        };

        HeroBorder.Background = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources[brush];
        StatusIcon.Source = icon;
        StatusLabel.Text = statusText;
    }

    private void OnSyncClicked(object? sender, EventArgs e) =>
        SyncRequested?.Invoke(this, EventArgs.Empty);

    private void OnCardTapped(object? sender, TappedEventArgs e) =>
        MemberTapped?.Invoke(this, EventArgs.Empty);
}
