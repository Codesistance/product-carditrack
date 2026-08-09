using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

public partial class StatusHeroCard : ContentView
{
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

        // Initials stay behind the photo rather than being replaced, so a photo that fails to
        // load falls back to something rather than an empty tile. PhotoUrl is external data, so
        // a relative or malformed value must fall back too rather than throw the whole load.
        var hasPhoto = Uri.TryCreate(data.PhotoUrl, UriKind.Absolute, out var photoUri);
        PhotoImage.Source = hasPhoto ? ImageSource.FromUri(photoUri!) : null;
        PhotoImage.IsVisible = hasPhoto;

        var (colorKey, icon, statusText) = data.HealthStatus switch
        {
            "green" => ("StatusGreen", "icon_status_check.svg", $"{firstName} is doing well"),
            "yellow" => ("StatusYellow", "icon_status_warning.svg", "Something looks a little different"),
            "orange" => ("StatusOrange", "icon_status_urgent.svg", "You should check in"),
            "red" => ("StatusRed", "icon_status_critical.svg", $"Reach out to {firstName} now"),
            // Paused is not a health reading — never dress it up as one.
            "paused" => ("StatusUnknown", "icon_status_paused.svg", $"Monitoring is paused for {firstName}"),
            _ => ("StatusUnknown", "icon_status_check.svg", "Getting to know their routine"),
        };

        StatusLabel.TextColor = (Color)Microsoft.Maui.Controls.Application.Current!.Resources[colorKey];
        StatusIcon.Source = icon;
        StatusLabel.Text = statusText;
    }

    private void OnCardTapped(object? sender, TappedEventArgs e) =>
        MemberTapped?.Invoke(this, EventArgs.Empty);
}
