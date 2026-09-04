using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

public partial class AlertListCard : ContentView
{
    /// <summary>Dimming applied to a phone action when its number is not on file.</summary>
    private const double UnavailableActionOpacity = 0.4;

    public event EventHandler<AlertSummaryResponse>? CallRequested;
    /// <summary>The emergency contact, not the member — the dashboard's SOS, on the card.</summary>
    public event EventHandler<AlertSummaryResponse>? SosRequested;
    public event EventHandler<AlertSummaryResponse>? AcknowledgeRequested;
    public event EventHandler<AlertSummaryResponse>? DeleteRequested;
    public event EventHandler<AlertSummaryResponse>? OpenRequested;

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

        Avatar.Apply(alert.CardiMemberName, alert.CardiMemberPhotoUrl);
        TitleLabel.Text = alert.Title;
        // Who and when share the line under the title: "Dad, 6 days ago". An alert whose member
        // name comes back blank (the field is never null, but it can be empty) just says when.
        var when = RelativeTime.Format(alert.TriggeredAt);
        MemberLabel.Text = string.IsNullOrWhiteSpace(alert.CardiMemberName)
            ? when
            : $"{alert.CardiMemberName}, {when}";
        MessageLabel.Text = alert.Message;

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;

        // Badge wording follows the M1-10 spec (CRITICAL/URGENT/INFO); the colour follows the
        // app's own severity scale rather than Figma's blue "Info" chip, so a yellow alert can't
        // show a yellow rail beside a blue badge.
        // NOTICE, not INFO, for yellow. The badge word and the banner fill are both read off
        // severity, so a yellow alert used to put the mildest word in the vocabulary on an amber
        // banner and read as a contradiction — and "INFO" was doing duty for green as well, which
        // flattened "nothing to report" and "something is different" into one word. The colour is
        // deliberately unchanged: see AlertListCard, which colours by our own severity scale
        // rather than by Figma's blue INFO chip so a badge can never disagree with the rail
        // beside it. This diverges from M1-10's CRITICAL/URGENT/INFO wording on purpose.
        var (badge, severityKey) = alert.Severity switch
        {
            "red" => ("CRITICAL", "StatusRed"),
            "orange" => ("URGENT", "StatusOrange"),
            "yellow" => ("NOTICE", "StatusYellow"),
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

        // "them" when the list carries no name, so the tooltips below stay sentences.
        var firstName = NameFormatting.FirstName(alert.CardiMemberName);
        var who = string.IsNullOrWhiteSpace(firstName) ? "them" : firstName;

        // Call reaches the member; SOS reaches their emergency contact — the dashboard's two
        // phone tiles, meaning the same things here. Each dims rather than hides when its number
        // is missing, so the tap can explain and offer to add it.
        var hasPhone = !string.IsNullOrWhiteSpace(alert.CardiMemberPhone);
        CallButton.Opacity = hasPhone ? 1 : UnavailableActionOpacity;
        ToolTipProperties.SetText(
            CallButton,
            hasPhone ? $"Calls {who}." : $"No phone number on file for {who} yet.");

        // Only the alerts that could warrant an emergency call offer one: a notice about a
        // quiet day should not put a red SOS beside itself.
        SosButton.IsVisible = alert.Severity is "orange" or "red";
        var hasEmergency = !string.IsNullOrWhiteSpace(alert.EmergencyContactPhone);
        SosButton.Opacity = hasEmergency ? 1 : UnavailableActionOpacity;
        ToolTipProperties.SetText(
            SosButton,
            hasEmergency
                ? $"Calls {(string.IsNullOrWhiteSpace(alert.EmergencyContactName) ? "the emergency contact" : alert.EmergencyContactName)}."
                : $"No emergency contact number on file for {who} yet.");

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

    private void OnOpenTapped(object? sender, TappedEventArgs e)
    {
        if (_alert is { } alert)
            OpenRequested?.Invoke(this, alert);
    }

    private void OnCallTapped(object? sender, TappedEventArgs e)
    {
        if (_alert is { } alert)
            CallRequested?.Invoke(this, alert);
    }

    private void OnSosTapped(object? sender, TappedEventArgs e)
    {
        if (_alert is { } alert)
            SosRequested?.Invoke(this, alert);
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
