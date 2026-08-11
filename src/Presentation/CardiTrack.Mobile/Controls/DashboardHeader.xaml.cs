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

    /// <param name="name">The caregiver's first name, or a time-of-day greeting when it isn't known.</param>
    /// <param name="context">A short, quiet time-of-day line under the name — never blank in
    /// the layout even when there's nothing else to say, so the header's height doesn't jump.</param>
    public void SetGreeting(string name, string context)
    {
        NameLabel.Text = name;
        ContextLabel.Text = context;
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
