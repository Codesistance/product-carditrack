namespace CardiTrack.Mobile.Controls;

/// <summary>
/// M1-09 gradient header band (Figma 101:2872): greeting, presence line, refresh and the
/// unread-alert bell.
/// </summary>
public partial class DashboardHeader : ContentView
{
    public event EventHandler? RefreshRequested;
    public event EventHandler? BellTapped;

    public DashboardHeader()
    {
        InitializeComponent();
    }

    /// <summary>Disabled while a sync is in flight so the tap can't be queued twice.</summary>
    public bool IsRefreshEnabled
    {
        get => RefreshButton.IsEnabled;
        set => RefreshButton.IsEnabled = value;
    }

    public void SetGreeting(string greeting) => GreetingLabel.Text = greeting;

    /// <summary>
    /// The presence line reports the wearer's monitoring state, not the caregiver's — silence
    /// must never read as "healthy", so a paused or disconnected member says so here.
    /// </summary>
    public void SetPresence(string presence) => PresenceLabel.Text = presence;

    /// <summary>
    /// A small clarifying line under the presence label — "Active now" on its own reads as
    /// "the wearer is currently moving"; this spells out that it means "we're actively
    /// monitoring, last synced X ago" instead. Hidden when there's nothing to add.
    /// </summary>
    public void SetPresenceDetail(string? detail)
    {
        PresenceDetailLabel.Text = detail;
        PresenceDetailLabel.IsVisible = !string.IsNullOrEmpty(detail);
    }

    /// <summary>
    /// Shows a dot when completeness items are waiting. Deliberately not folded into
    /// <see cref="SetUnreadCount"/>: the number means health alerts and must keep meaning that,
    /// or it stops carrying urgency by the time a red alert finally arrives.
    /// </summary>
    public void SetNudgeIndicator(bool hasNudges) => NudgeDot.IsVisible = hasNudges;

    public void SetUnreadCount(int count)
    {
        BellBadge.IsVisible = count > 0;
        BellBadgeLabel.Text = count > 9 ? "9+" : count.ToString();

        // The badge is the only thing that carries the count on screen, so the accessible
        // name has to say it too or the number is lost to a screen reader.
        SemanticProperties.SetDescription(BellButton, count switch
        {
            <= 0 => "Alerts",
            1 => "Alerts, 1 unread",
            _ => $"Alerts, {count} unread",
        });
    }

    private void OnRefreshClicked(object? sender, EventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnBellClicked(object? sender, EventArgs e) =>
        BellTapped?.Invoke(this, EventArgs.Empty);
}
