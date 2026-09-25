using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Alerts;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// One CardiMember on the dashboard. The dashboard stacks one per member the family watches,
/// primary first; everything here is about the member in <see cref="Data"/> and nobody else.
/// </summary>
/// <remarks>
/// Moved out of <c>DashboardPage</c>, which drew exactly one member into fixed controls. The
/// rendering is unchanged; what changed is that the page now asks each card for its member rather
/// than keeping one member in fields of its own, so every tap below carries the card it came from.
/// </remarks>
public partial class MemberDashboardCard : ContentView
{
    /// <summary>Columns in the Key Metrics grid; see <see cref="LayoutMetricCards"/>.</summary>
    private const int MetricsPerRow = 2;

    /// <summary>
    /// The last Sleep alert whose nudge was dismissed, per member — a local convenience, never an
    /// acknowledgement. Keyed by member so dismissing one person's does not bring back another's.
    /// </summary>
    private static string DismissedSleepAlertKey(Guid memberId) => $"DismissedSleepAlertId:{memberId:N}";

    private Guid? _currentSleepAlertId;

    public MemberDashboardCard()
    {
        InitializeComponent();
        Hero.MemberTapped += (_, _) => DetailsRequested?.Invoke(this, EventArgs.Empty);
        Hero.DaybookTapped += (_, _) => DaybookRequested?.Invoke(this, EventArgs.Empty);
        Hero.AlertsTapped += (_, _) => AlertsRequested?.Invoke(this, EventArgs.Empty);
        Hero.NoDeviceTapped += (_, _) => NoDeviceRequested?.Invoke(this, EventArgs.Empty);
        Hero.QaTapped += (_, _) => QuestionRequested?.Invoke(this, EventArgs.Empty);
        Hero.AdviseTapped += (_, _) => AdviseRequested?.Invoke(this, EventArgs.Empty);
        Hero.WeatherTapped += (_, weather) => WeatherRequested?.Invoke(this, weather);
    }

    /// <summary>The dashboard this card last drew, or null before its first.</summary>
    public DashboardResponse? Data { get; private set; }

    /// <summary>The member card at the top, for the live status line the page fetches separately.</summary>
    public StatusHeroCard HeroCard => Hero;

    public event EventHandler? DetailsRequested;
    public event EventHandler? DaybookRequested;
    public event EventHandler? AlertsRequested;
    public event EventHandler? NoDeviceRequested;
    public event EventHandler? QuestionRequested;
    public event EventHandler? AdviseRequested;
    public event EventHandler<WeatherSnapshotResponse>? WeatherRequested;
    public event EventHandler<Guid>? SleepAlertRequested;

    /// <summary>
    /// Marks the member the dashboard treats as primary, when there is more than one to tell apart.
    /// </summary>
    public void SetPrimary(bool isPrimary) => Hero.SetPrimary(isPrimary);

    public void Apply(DashboardResponse data, IPopupService popups)
    {
        Data = data;
        Hero.Apply(data);

        var firstName = data.DisplayFirstName();
        QuickActions.Apply(
            new QuickActionTarget(
                data.CardiMemberId,
                firstName,
                data.Phone,
                data.EmergencyContactPhone,
                data.EmergencyContactName),
            popups);

        // Paused banner (M1-13)
        PausedBanner.IsVisible = data.MonitoringPaused;
        if (data.MonitoringPaused)
        {
            var until = data.MonitoringPausedUntil is { } pausedUntil
                ? DateTime.SpecifyKind(pausedUntil, DateTimeKind.Utc).ToLocalTime().ToString("MMM d, h:mm tt")
                : "further notice";
            PausedBannerLabel.Text =
                $"Monitoring {firstName} is paused until {until} — we're not collecting data or raising alerts.";
        }

        // No device (M1-09d): the struck-through watch beside Alerts on the member card.
        Hero.SetNoDevice(!data.Device.HasActiveConnection);

        ApplyFreshness(data);
        ApplySleepConcern(data, firstName);
        ApplyMetrics(data);
        ApplyReassurance(data, firstName);
    }

    /// <summary>
    /// Data-pipeline freshness (deterministic — see MemberInsightsCalculator). Suppressed while
    /// paused: collection is intentionally stopped, so a freshness reading would misreport a
    /// deliberate pause as a gap.
    /// </summary>
    private void ApplyFreshness(DashboardResponse data)
    {
        var freshnessRelevant = !data.MonitoringPaused;
        var freshnessColor = FreshnessPalette.ColorFor(data.DataFreshness);

        // Silent while the data is arriving as it should: readings come every ten minutes, and a
        // caption that appears when nothing is wrong cannot mean anything when something is.
        // Never-synced keeps its own line: that is a different statement.
        var neverSynced = data.LastSyncedAt is null;
        var ageWorthShowing = DataAge.IsWorthShowing(data.LastSyncedAt, DateTime.UtcNow);

        var showAge = freshnessRelevant && (neverSynced || ageWorthShowing);
        LastUpdatedFooterLabel.IsVisible = showAge;
        LastUpdatedFooterLabel.Text = data.LastSyncedAt is { } lastSynced
            ? $"Updated {RelativeTime.Format(lastSynced)}"
            : "Not synced yet";
        LastUpdatedFooterLabel.TextColor = freshnessColor;
        SemanticProperties.SetDescription(
            LastUpdatedFooterLabel, $"{data.DataFreshnessMessage}. {LastUpdatedFooterLabel.Text}");

        // Exactly one node announces the freshness state, and only while there is a state worth
        // announcing — the label whenever it is on screen, the block only while the learning bar
        // alone keeps it there.
        SemanticProperties.SetDescription(
            FreshnessBlock, showAge ? string.Empty : data.DataFreshnessMessage);

        // Baseline-learning progress only while the window is still running.
        LearningProgress.IsVisible = data.Baseline.IsLearning && data.Device.HasActiveConnection
            && !data.MonitoringPaused;
        LearningProgress.Progress = data.Baseline.PercentComplete / 100.0;
        LearningProgress.ProgressColor = freshnessColor;

        // Collapses when neither child has anything to say, which on a healthy dashboard is the
        // usual case — an empty box would be a gap under the hero card on every normal day.
        FreshnessBlock.IsVisible = freshnessRelevant
            && (LastUpdatedFooterLabel.IsVisible || LearningProgress.IsVisible);
    }

    /// <summary>
    /// Poor-sleep nudge: points at the real, unacknowledged Sleep alert the statistical pass
    /// already raised, rather than a second judgement derived from today's metric alone.
    /// </summary>
    private void ApplySleepConcern(DashboardResponse data, string firstName)
    {
        var sleepAlert = data.RecentAlerts.FirstOrDefault(a => a.Type == "Sleep" && a.Status == "new");
        var dismissedId = Preferences.Default.Get(DismissedSleepAlertKey(data.CardiMemberId), string.Empty);
        _currentSleepAlertId = sleepAlert?.AlertId;
        SleepConcernBanner.IsVisible = sleepAlert is not null
            && sleepAlert.AlertId.ToString() != dismissedId;
        if (SleepConcernBanner.IsVisible)
            SleepConcernBannerLabel.Text = $"{firstName}'s sleep has looked different than usual lately. Tap to view.";
    }

    private void ApplyMetrics(DashboardResponse data)
    {
        if (data.Metrics is not { } metrics)
        {
            MetricsAccordion.IsVisible = false;
            return;
        }

        MetricsAccordion.IsVisible = true;
        StepsCard.ApplySteps(metrics.Steps);
        HeartRateCard.ApplyHeartRate(metrics.RestingHeartRate);
        SleepCard.ApplySleep(metrics.Sleep);

        // Not every connected device reports these, so the tile disappears entirely rather than
        // showing a permanent "—" for a member whose wearable never will.
        TemperatureCard.IsVisible = metrics.Temperature.Value is not null;
        if (TemperatureCard.IsVisible)
            TemperatureCard.ApplyTemperature(metrics.Temperature);

        SpO2Card.IsVisible = metrics.SpO2.Value is not null;
        if (SpO2Card.IsVisible)
            SpO2Card.ApplySpO2(metrics.SpO2);

        BreathingRateCard.IsVisible = metrics.BreathingRate.Value is not null;
        if (BreathingRateCard.IsVisible)
            BreathingRateCard.ApplyBreathingRate(metrics.BreathingRate);

        LayoutMetricCards();
    }

    /// <summary>
    /// The "all quiet" line, from the server's verdict rather than an empty alert list — which is
    /// equally empty for a paused member, a silent watch, a member signed up yesterday and one in
    /// the middle of an episode somebody has seen but nobody has closed (see QuietStretch).
    /// </summary>
    private void ApplyReassurance(DashboardResponse data, string firstName)
    {
        var reassurance = data.RecentAlerts.Count == 0 ? data.Reassurance : null;
        ReassuranceRow.IsVisible = reassurance is not null;
        if (reassurance is null)
            return;

        ReassuranceTitleLabel.Text = ReassuranceCopy.Title;
        ReassuranceDetailLabel.Text = ReassuranceCopy.Detail(reassurance, firstName);
        SemanticProperties.SetDescription(
            ReassuranceRow, $"{ReassuranceTitleLabel.Text}. {ReassuranceDetailLabel.Text}");
    }

    /// <summary>
    /// Packs the Key Metrics tiles two to a row in reading order — the night first, then the day —
    /// skipping the ones this member's wearable doesn't report, so no tile sits beside a gap.
    /// </summary>
    private void LayoutMetricCards()
    {
        var slot = 0;
        foreach (var card in new[] { HeartRateCard, SleepCard, TemperatureCard, StepsCard, SpO2Card, BreathingRateCard })
        {
            if (!card.IsVisible)
                continue;

            Grid.SetRow(card, slot / MetricsPerRow);
            Grid.SetColumn(card, slot % MetricsPerRow);
            slot++;
        }
    }

    private void OnDismissSleepConcernClicked(object? sender, EventArgs e)
    {
        if (_currentSleepAlertId is { } id && Data is { } data)
            Preferences.Default.Set(DismissedSleepAlertKey(data.CardiMemberId), id.ToString());
        SleepConcernBanner.IsVisible = false;
    }

    /// <summary>The nudge is about one particular Sleep alert, so it opens that alert.</summary>
    private void OnSleepConcernTapped(object? sender, TappedEventArgs e)
    {
        if (_currentSleepAlertId is { } alertId)
            SleepAlertRequested?.Invoke(this, alertId);
    }
}
