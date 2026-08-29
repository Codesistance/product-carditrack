namespace CardiTrack.Mobile.Controls;

/// <summary>
/// M1-09 gradient header band (Figma 101:2872): greeting, presence line, refresh and the
/// unread-alert bell.
/// </summary>
public partial class DashboardHeader : ContentView
{
    private const string BellShakeAnimation = "bell-shake";

    /// <summary>
    /// One cycle of the shake: a short burst of swing, then quiet. Longer than the burst it
    /// contains on purpose — a bell that never stops moving stops being a signal and becomes
    /// wallpaper, and this one runs for as long as an alert is unread, which can be hours.
    /// </summary>
    private const uint BellShakeCycleMs = 3600;

    public event EventHandler? BellTapped;

    /// <summary>
    /// What <see cref="SetUnreadCount"/> last recorded — read by the shake's repeat predicate,
    /// so the loop ends on the tick after the last alert is acknowledged rather than needing
    /// the caller to stop it.
    /// </summary>
    private int _unreadCount;

    public DashboardHeader()
    {
        InitializeComponent();

        // Same lifecycle discipline as StatusHeroCard's pulses: a header taken off screen with
        // alerts still unread must not leave its animation looping in the background. The
        // dashboard reloads on the way back in, and that call starts it again.
        Unloaded += (_, _) => StopBellShake();
    }

    /// <summary>Disabled while a sync is in flight so the tap can't be queued twice.</summary>

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
        _unreadCount = count;
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

        // A red dot on a header a caregiver has already looked past says nothing. The bell moves
        // for as long as something is unread — and stops the moment the last one is
        // acknowledged, which is what makes the movement mean anything.
        if (count > 0)
            StartBellShake();
        else
            StopBellShake();
    }

    /// <summary>
    /// Started only when one isn't already running, the same reason
    /// <see cref="StatusHeroCard"/>'s Advise pulse is: the dashboard reloads itself every thirty
    /// seconds, and restarting on each of those would snap the bell back to its first frame
    /// mid-swing. Asking the animation rather than tracking the previous count is also what
    /// makes this self-heal after <c>Unloaded</c> has stopped the loop.
    /// </summary>
    private void StartBellShake()
    {
        if (this.AnimationIsRunning(BellShakeAnimation))
            return;

        // Swings decay — 12°, 12°, 9°, 6° — so the burst reads as a bell struck once and left to
        // settle, not as a metronome. The whole of it lands in the first 28% of the cycle; the
        // rest is the rest.
        var shake = new Animation
        {
            { 0.00, 0.05, new Animation(v => BellButton.Rotation = v, 0, -12, Easing.CubicOut) },
            { 0.05, 0.11, new Animation(v => BellButton.Rotation = v, -12, 12, Easing.SinInOut) },
            { 0.11, 0.17, new Animation(v => BellButton.Rotation = v, 12, -9, Easing.SinInOut) },
            { 0.17, 0.23, new Animation(v => BellButton.Rotation = v, -9, 6, Easing.SinInOut) },
            { 0.23, 0.28, new Animation(v => BellButton.Rotation = v, 6, 0, Easing.CubicIn) },
        };

        shake.Commit(this, BellShakeAnimation, length: BellShakeCycleMs,
            repeat: () => IsLoaded && _unreadCount > 0);
    }

    private void StopBellShake()
    {
        this.AbortAnimation(BellShakeAnimation);

        // Aborting leaves the bell wherever the frame it was cut on had it — put it back upright.
        BellButton.Rotation = 0;
    }

    private void OnBellClicked(object? sender, EventArgs e) =>
        BellTapped?.Invoke(this, EventArgs.Empty);
}
