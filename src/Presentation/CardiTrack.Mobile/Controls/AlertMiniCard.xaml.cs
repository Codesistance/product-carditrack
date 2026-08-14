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

        var (tileKey, pillKey, inkKey) = alert.Severity switch
        {
            "red" => ("AlertTileRed", "PillRedBackground", "StatusRed"),
            "orange" => ("AlertTileOrange", "PillOrangeBackground", "StatusOrange"),
            "yellow" => ("AlertTileYellow", "PillYellowBackground", "StatusYellow"),
            _ => ("AlertTileNeutral", "PillNeutralBackground", "StatusUnknown"),
        };

        IconTileBorder.BackgroundColor = (Color)resources[tileKey];

        // Same three words the alerts list uses (see AlertListCard) — this pill used to read
        // "Resolved" for a merely acknowledged alert, which told a caregiver the episode was over
        // when all it recorded was that somebody had looked. An acknowledged alert reads as
        // settled whatever its severity was; an open one keeps the severity colour so a red alert
        // can't be mistaken for handled. "resolved" never reaches this strip — the dashboard
        // carries unresolved alerts only — but it is mapped rather than left to the fallback so a
        // resolved one could not arrive here wearing the "New" pill.
        var (statusText, backgroundKey, inkColorKey) = alert.Status switch
        {
            "resolved" => ("Resolved", "PillGreenBackground", "StatusGreen"),
            "acknowledged" => ("Acknowledged", "PillGreenBackground", "StatusGreen"),
            _ => ("New", pillKey, inkKey),
        };

        StatusPillLabel.Text = statusText;
        StatusPillBorder.BackgroundColor = (Color)resources[backgroundKey];
        StatusPillLabel.TextColor = (Color)resources[inkColorKey];
    }

    private void OnTapped(object? sender, EventArgs e) =>
        AlertTapped?.Invoke(this, _alertId);
}
