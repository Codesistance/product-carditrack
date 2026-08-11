using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

public partial class StatusHeroCard : ContentView
{
    /// <summary>Raised when the card body is tapped — the dashboard's route into M1-13.</summary>
    public event EventHandler? MemberTapped;

    /// <summary>
    /// Which tier <see cref="Apply"/> last rendered, so a late-arriving
    /// <see cref="ApplyDynamicMessage"/> can tell whether it's still describing the status
    /// actually on screen.
    /// </summary>
    private string? _healthStatus;

    public StatusHeroCard()
    {
        InitializeComponent();
    }

    public void Apply(DashboardResponse data)
    {
        var firstName = NameFormatting.FirstName(data.Name);
        NameLabel.Text = $"{data.Name}, {data.Age}";
        InitialsLabel.Text = NameFormatting.Initials(data.Name);

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
        _healthStatus = data.HealthStatus;
    }

    /// <summary>
    /// Swaps in a live, MedGemma-generated line over the static per-tier copy <see cref="Apply"/>
    /// already rendered. Ignored if the card has since moved to a different status — a refresh
    /// landing while the call was still in flight — since the message would describe a tier
    /// that's no longer showing.
    /// </summary>
    public void ApplyDynamicMessage(string message, string forHealthStatus)
    {
        if (forHealthStatus != _healthStatus || string.IsNullOrWhiteSpace(message))
            return;

        StatusLabel.Text = message;
        StatusLabel.Opacity = 0;
        _ = StatusLabel.FadeToAsync(1, 150, Easing.CubicOut);
    }

    private void OnCardTapped(object? sender, TappedEventArgs e) =>
        MemberTapped?.Invoke(this, EventArgs.Empty);
}
