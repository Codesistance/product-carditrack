using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

public partial class AlertListCard : ContentView
{
    /// <summary>Dimming applied to the Call button when the member has no number on file.</summary>
    private const double UnavailableActionOpacity = 0.4;

    public event EventHandler<AlertSummaryResponse>? CallRequested;
    public event EventHandler<AlertSummaryResponse>? AcknowledgeRequested;
    public event EventHandler<AlertSummaryResponse>? DeleteRequested;

    private AlertSummaryResponse? _alert;
    private bool _isExpanded;

    public AlertListCard()
    {
        InitializeComponent();
    }

    public Guid AlertId => _alert?.AlertId ?? Guid.Empty;

    public void Apply(AlertSummaryResponse alert)
    {
        _alert = alert;

        InitialsLabel.Text = NameFormatting.Initials(alert.CardiMemberName);
        TitleLabel.Text = alert.Title;
        MemberLabel.Text = alert.CardiMemberName;
        MemberLabel.IsVisible = !string.IsNullOrWhiteSpace(alert.CardiMemberName);
        MessageLabel.Text = alert.Message;
        TimeLabel.Text = RelativeTime.Format(alert.TriggeredAt);

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;

        // Badge wording follows the M1-10 spec (CRITICAL/URGENT/INFO); the colour follows the
        // app's own severity scale rather than Figma's blue "Info" chip, so a yellow alert can't
        // show a yellow rail beside a blue badge.
        var (badge, severityKey) = alert.Severity switch
        {
            "red" => ("CRITICAL", "StatusRed"),
            "orange" => ("URGENT", "StatusOrange"),
            "yellow" => ("INFO", "StatusYellow"),
            "green" => ("INFO", "StatusGreen"),
            _ => ("INFO", "StatusUnknown"),
        };

        var severityColor = (Color)resources[severityKey];
        SeverityLabel.Text = badge;
        SeverityPill.BackgroundColor = severityColor;
        SeverityRail.BackgroundColor = severityColor;

        var (statusText, pillKey, inkKey) = alert.Status switch
        {
            "resolved" => ("Resolved", "PillGreenBackground", "StatusGreen"),
            "acknowledged" => ("Acknowledged", "PillGreenBackground", "StatusGreen"),
            _ => ("New", "PillNeutralBackground", "Primary"),
        };

        StatusPillLabel.Text = statusText;
        StatusPillBorder.BackgroundColor = (Color)resources[pillKey];
        StatusPillLabel.TextColor = (Color)resources[inkKey];

        var isHandled = alert.Status != "new";
        UnreadDot.IsVisible = !isHandled;
        // Nothing left to acknowledge once it is handled — the status pill already says so.
        AcknowledgeButton.IsVisible = !isHandled;

        var hasPhone = !string.IsNullOrWhiteSpace(alert.EmergencyContactPhone);
        CallButton.Opacity = hasPhone ? 1 : UnavailableActionOpacity;
        ToolTipProperties.SetText(
            CallButton,
            hasPhone
                ? $"Calls {alert.EmergencyContactName ?? "the emergency contact"}."
                : $"No number on file for {NameFormatting.FirstName(alert.CardiMemberName)} yet.");

        SetBusy(false);
        SetExpanded(_isExpanded);
    }

    /// <summary>Shows a spinner in place of the check while the acknowledgement is in flight.</summary>
    public void SetBusy(bool busy)
    {
        AcknowledgeSpinner.IsVisible = busy;
        AcknowledgeSpinner.IsRunning = busy;
        AcknowledgeIcon.IsVisible = !busy;
        AcknowledgeButton.InputTransparent = busy;
    }

    private void SetExpanded(bool expanded)
    {
        _isExpanded = expanded;

        // -1 is MAUI's "no limit"; the collapsed state is the designed 2-line preview.
        MessageLabel.MaxLines = expanded ? -1 : 2;
        MessageLabel.LineBreakMode = expanded ? LineBreakMode.WordWrap : LineBreakMode.TailTruncation;
        ExpandChevron.Rotation = expanded ? 180 : 0;
    }

    private void OnExpandTapped(object? sender, TappedEventArgs e) => SetExpanded(!_isExpanded);

    private void OnCallTapped(object? sender, TappedEventArgs e)
    {
        if (_alert is { } alert)
            CallRequested?.Invoke(this, alert);
    }

    private void OnAcknowledgeTapped(object? sender, TappedEventArgs e)
    {
        if (_alert is { } alert)
            AcknowledgeRequested?.Invoke(this, alert);
    }

    private void OnDeleteTapped(object? sender, TappedEventArgs e)
    {
        if (_alert is { } alert)
            DeleteRequested?.Invoke(this, alert);
    }
}
