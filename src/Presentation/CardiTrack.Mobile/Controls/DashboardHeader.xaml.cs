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

    public void SetUnreadCount(int count)
    {
        BellBadge.IsVisible = count > 0;
        BellBadgeLabel.Text = count > 9 ? "9+" : count.ToString();
    }

    private void OnRefreshClicked(object? sender, EventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnBellClicked(object? sender, EventArgs e) =>
        BellTapped?.Invoke(this, EventArgs.Empty);
}
