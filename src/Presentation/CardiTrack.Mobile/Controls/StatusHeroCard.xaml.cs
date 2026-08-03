using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

public partial class StatusHeroCard : ContentView
{
    public event EventHandler? SyncRequested;

    public StatusHeroCard()
    {
        InitializeComponent();
    }

    public void Apply(DashboardResponse data)
    {
        var firstName = data.Name.Split(' ')[0];
        NameLabel.Text = $"{data.Name}, {data.Age}";
        InitialsLabel.Text = Initials(data.Name);
        LastSyncedLabel.Text = data.LastSyncedAt is { } synced
            ? $"Updated {RelativeTime.Format(synced)}"
            : "Not synced yet";

        var (brush, icon, statusText) = data.HealthStatus switch
        {
            "green" => ("HeroGreenBrush", "icon_status_check.svg", $"{firstName} is doing well"),
            "yellow" => ("HeroYellowBrush", "icon_status_warning.svg", "Something looks a little different"),
            "orange" => ("HeroOrangeBrush", "icon_status_urgent.svg", "You should check in"),
            "red" => ("HeroRedBrush", "icon_status_critical.svg", $"Reach out to {firstName} now"),
            _ => ("HeroUnknownBrush", "icon_status_check.svg", "Getting to know their routine"),
        };

        HeroBorder.Background = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources[brush];
        StatusIcon.Source = icon;
        StatusLabel.Text = statusText;
    }

    private void OnSyncClicked(object? sender, EventArgs e) =>
        SyncRequested?.Invoke(this, EventArgs.Empty);

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => char.ToUpperInvariant(parts[0][0]).ToString(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}",
        };
    }
}
