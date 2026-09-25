using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Forms;
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
        MessageLabel.Text = alert.Message;
        MessageLabel.IsVisible = !string.IsNullOrWhiteSpace(alert.Message);
        TimeLabel.Text = RelativeTime.Format(alert.TriggeredAt);

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;

        // Display names from AlertType; anything unmapped falls back to the warning glyph
        // rather than to no icon, so the tile is never empty.
        TypeIcon.Source = alert.Type switch
        {
            "Inactivity" => "icon_metric_steps.svg",
            "Heart Rate" => "icon_metric_heart.svg",
            "Sleep" => "icon_metric_sleep.svg",
            _ => "icon_status_warning.svg",
        };

        // No status pill: DashboardResponse.RecentAlerts carries unacknowledged alerts only, so
        // every card in this strip would say "New" — a word that never changes tells a caregiver
        // nothing. Acknowledged and resolved alerts are read on the alerts list, which keeps the
        // pill (see AlertListCard). Severity is the one thing that varies, and the rail and the
        // tile both carry it.
        var (tileKey, railKey) = alert.Severity switch
        {
            "red" => ("AlertTileRed", "StatusRed"),
            "orange" => ("AlertTileOrange", "StatusOrange"),
            "yellow" => ("AlertTileYellow", "StatusYellow"),
            "green" => ("AlertTileGreen", "StatusGreen"),
            _ => ("AlertTileNeutral", "StatusUnknown"),
        };

        IconTileBorder.BackgroundColor = (Color)resources[tileKey];
        SeverityRail.BackgroundColor = (Color)resources[railKey];
    }

    private void OnTapped(object? sender, EventArgs e) =>
        AlertTapped?.Invoke(this, _alertId);
}
