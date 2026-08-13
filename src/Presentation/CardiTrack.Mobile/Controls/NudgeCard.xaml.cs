using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Notifications;
// CardiTrack.Application (the DTO assembly's root namespace) shadows MAUI's Application in
// any file importing it, so the control type is aliased rather than qualified at each use.
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace CardiTrack.Mobile.Controls;

/// <summary>One notification in the inbox, with its three affordances.</summary>
public partial class NudgeCard : ContentView
{
    /// <summary>Comply — the caregiver wants to fix it now. Carries the deep link to follow.</summary>
    public event EventHandler<NotificationResponse>? ComplyRequested;

    /// <summary>Not now.</summary>
    public event EventHandler<NotificationResponse>? SnoozeRequested;

    /// <summary>Don't ask again.</summary>
    public event EventHandler<NotificationResponse>? DismissRequested;

    private NotificationResponse? _notification;

    public NudgeCard()
    {
        InitializeComponent();
    }

    public void Bind(NotificationResponse notification)
    {
        _notification = notification;

        TitleLabel.Text = NudgeCopy.Title(notification);
        BodyLabel.Text = NudgeCopy.Body(notification);
        BenefitLabel.Text = NudgeCopy.Benefit(notification);

        PriorityRail.BackgroundColor = RailColour(notification);
        ComplyButton.Text = ComplyVerb(notification.RuleCode);

        // A relative who does not own the item sees it and nothing else — no buttons to press,
        // so no chance of two people fixing the same thing or neither doing it.
        ActionRow.IsVisible = notification.IsOwner;
        FamilyPill.IsVisible = !notification.IsOwner;

        DismissButton.IsVisible = notification.CanMute;
    }

    /// <summary>
    /// Safety and Critical share the alert palette's red; the rest step down through the same
    /// status colours the dashboard uses, so urgency reads consistently across both surfaces.
    /// </summary>
    private static Color RailColour(NotificationResponse n)
    {
        var key = n.Category == NotificationCategory.Safety
            ? "StatusRed"
            : n.Priority switch
            {
                NotificationPriority.Critical => "StatusRed",
                NotificationPriority.High => "StatusOrange",
                NotificationPriority.Medium => "StatusYellow",
                _ => "Primary"
            };

        return MauiApplication.Current?.Resources.TryGetValue(key, out var value) == true && value is Color colour
            ? colour
            : Colors.Gray;
    }

    /// <summary>
    /// The button says what tapping it does, not "Fix" — a caregiver should know whether they are
    /// about to open a settings screen or start an OAuth flow before they commit to it.
    /// </summary>
    private static string ComplyVerb(string ruleCode) => ruleCode switch
    {
        "DEVICE_AUTH_BROKEN" => "Reconnect",
        // Not "Charge" — the app cannot charge anything, and the only thing tapping does is open
        // the device list so the caregiver can see which wearable it is.
        "DEVICE_BATTERY_LOW" => "View device",
        "DEVICE_REMOVED" => "Connect a device",
        "DEVICE_STALE_LONG" => "View device",
        "TIMEZONE_DEFAULT" => "Set time zone",
        "BASELINE_STALLED" => "See progress",
        "SLEEP_SCOPE_MISSING" => "Grant sleep access",
        "MEDICAL_NOTES_EMPTY" => "Add notes",
        "PAUSE_LEFT_LONG" => "Review pause",
        _ => "Open"
    };

    private void OnComplyClicked(object? sender, EventArgs e)
    {
        if (_notification is not null)
            ComplyRequested?.Invoke(this, _notification);
    }

    private void OnSnoozeClicked(object? sender, EventArgs e)
    {
        if (_notification is not null)
            SnoozeRequested?.Invoke(this, _notification);
    }

    private void OnDismissClicked(object? sender, EventArgs e)
    {
        if (_notification is not null)
            DismissRequested?.Invoke(this, _notification);
    }
}
