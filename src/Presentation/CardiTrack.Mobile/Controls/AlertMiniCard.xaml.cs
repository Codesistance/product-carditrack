using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

public partial class AlertMiniCard : ContentView
{
    public event EventHandler<Guid>? AlertTapped;

    private Guid _alertId;

    public AlertMiniCard()
    {
        InitializeComponent();
    }

    public void Apply(DashboardAlertSummary alert)
    {
        _alertId = alert.AlertId;
        TitleLabel.Text = alert.Title;
        TimeLabel.Text = RelativeTime.Format(alert.TriggeredAt);
        StatusChip.Text = alert.IsAcknowledged ? "Acknowledged" : "New";

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        SeverityBar.Color = alert.Severity switch
        {
            "red" => (Color)resources["StatusRed"],
            "orange" => (Color)resources["StatusOrange"],
            "yellow" => (Color)resources["StatusYellow"],
            _ => (Color)resources["StatusUnknown"],
        };
    }

    private void OnTapped(object? sender, EventArgs e) =>
        AlertTapped?.Invoke(this, _alertId);
}
