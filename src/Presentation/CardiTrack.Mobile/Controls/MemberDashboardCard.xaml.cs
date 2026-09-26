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

        // Every card opens Key Metrics from the Metrics button on its actions line, so the
        // accordion never draws a header of its own (ShowMetricsControls).
        MetricsAccordion.ShowHeader = false;

        // The footer line is a link only while it says "Connect a device" (ApplyFreshness).
        var connectTap = new TapGestureRecognizer();
        connectTap.Tapped += (_, _) =>
        {
            if (_connectLinkShown)
                NoDeviceRequested?.Invoke(this, EventArgs.Empty);
        };
        LastUpdatedFooterLabel.GestureRecognizers.Add(connectTap);
        Hero.WeatherTapped += (_, weather) => WeatherRequested?.Invoke(this, weather);
        Hero.PinTapped += (_, _) => PinRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The dashboard this card last drew, or null before its first.</summary>
    public DashboardResponse? Data { get; private set; }

    /// <summary>The member card at the top, for the live status line the page fetches separately.</summary>
    public StatusHeroCard HeroCard => Hero;

    public event EventHandler? DetailsRequested;
    public event EventHandler? DaybookRequested;
    public event EventHandler? AlertsRequested;
    public event EventHandler? NoDeviceRequested;

    /// <summary>The footer line is showing the "Connect a device" link rather than the sync age.</summary>
    private bool _connectLinkShown;
    public event EventHandler? QuestionRequested;
    public event EventHandler? AdviseRequested;
    public event EventHandler<WeatherSnapshotResponse>? WeatherRequested;
    public event EventHandler<Guid>? SleepAlertRequested;
    public event EventHandler? PinRequested;

    /// <summary>
    /// How the card sits among others: with more than one member on the dashboard it offers the
    /// pin after the member's name. One member has no pin, since there is nothing to rank it
    /// against.
    /// </summary>
    /// <remarks>
    /// This used to switch SOS / Call / Message / Details between the full labelled tiles for a
    /// lone member and the compact icon row for a stack. Since 2026-09-26 every card has the one
    /// actions line — Metrics, the card's news, and the three contact buttons, compact — because
    /// the news moved there from the hero's top row, and a line of full tiles leaves it no room.
    /// </remarks>
    public void SetStacking(bool several, bool pinned) => Hero.SetPinning(several, pinned);

    /// <summary>
    /// Key Metrics, opened from the Metrics button on the actions line, the accordion showing only
    /// while it is open so a closed one costs the card no row and no gap.
    /// </summary>
    private void ShowMetricsControls()
    {
        var hasMetrics = Data?.Metrics is not null;
        MetricsLink.IsVisible = hasMetrics;
        MetricsAccordion.IsVisible = hasMetrics && MetricsAccordion.IsExpanded;
        ShowMetricsState(MetricsAccordion.IsExpanded);
    }

    /// <summary>
    /// The Metrics button's chevron points up while the metrics are open, and the hint says what a
    /// tap will do — the chevron's direction is not something a screen reader can see.
    /// </summary>
    private void ShowMetricsState(bool expanded)
    {
        MetricsChevron.Rotation = expanded ? 180 : 0;
        SemanticProperties.SetHint(MetricsLink, expanded ? "Hides the key metrics" : "Shows the key metrics");
    }

    private async void OnMetricsLinkTapped(object? sender, TappedEventArgs e)
    {
        if (!MetricsAccordion.IsExpanded)
        {
            // Shown first, so the accordion has a width to measure its body against as it opens.
            MetricsAccordion.IsVisible = true;
            await Task.Yield();
            MetricsAccordion.Toggle();
            ShowMetricsState(true);
            return;
        }

        MetricsAccordion.Toggle();
        ShowMetricsState(false);
        // Hidden once the close has run, so the collapsed accordion leaves no gap behind it.
        await Task.Delay(220);
        if (!MetricsAccordion.IsExpanded)
            MetricsAccordion.IsVisible = false;
    }

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

        ApplyNews(data);
        ApplyFreshness(data);
        ApplySleepConcern(data, firstName);
        ApplyMetrics(data);
        ApplyReassurance(data, firstName);
    }

    /// <summary>
    /// The state behind the news buttons on the actions line, kept so a tap can mark what it
    /// opened as read (see <see cref="AttentionMarks"/>). A news button exists only while there is
    /// something behind it the caregiver has not taken in — where each used to swap to a plain
    /// glyph once read, it now goes altogether, and the way to a read journal or a quiet alerts
    /// list is the tab bar. Nothing animates: a row of rings going round on every landing was
    /// four things moving for a card whose whole job is to be read at a glance.
    /// </summary>
    private Guid _memberId;
    private DateTime? _latestJournalEntryAt;
    private DateTime? _adviseGeneratedAt;
    private bool _journalUnread;
    private bool _adviseUnread;
    private int _unreadAlertCount;
    private Guid? _pendingQuestionId;

    /// <summary>
    /// This member has no active device. The line then shows only the no-device button among the
    /// news: every other one — the journal, questions, alerts, the suggestion — is about readings
    /// a member with no device is not sending, so each was a door to an empty room.
    /// </summary>
    private bool _noDevice;

    /// <summary>The news buttons on the actions line, from the dashboard this card is drawing.</summary>
    private void ApplyNews(DashboardResponse data)
    {
        _memberId = data.CardiMemberId;

        // No device (M1-09d): the struck-through watch among the news on the member card.
        _noDevice = !data.Device.HasActiveConnection;

        ApplyDaybook(data.LatestJournalEntryAt);
        ApplyAdvise(data.HasAdvise, data.AdviseGeneratedAt);
        ApplyOpenAlerts(data.UnreadAlertCount);
        ApplyPendingQuestionnaire(data.PendingQuestionnaire);
        ShowNews();
    }

    /// <summary>
    /// Which news buttons are on the line, and the hairline between them and the contact buttons
    /// while any are. A stack lays them out, so a hidden one takes its spacing with it.
    /// </summary>
    private void ShowNews()
    {
        AlertsCluster.IsVisible = !_noDevice && _unreadAlertCount > 0;
        DaybookCluster.IsVisible = !_noDevice && _journalUnread;
        AdviseCluster.IsVisible = !_noDevice && _adviseUnread;
        QaCluster.IsVisible = !_noDevice && _pendingQuestionId is not null;
        NoDeviceButton.IsVisible = _noDevice;

        NewsDivider.IsVisible = AlertsCluster.IsVisible || DaybookCluster.IsVisible
            || AdviseCluster.IsVisible || QaCluster.IsVisible || NoDeviceButton.IsVisible;
    }

    /// <summary>
    /// The Q&amp;A button, from <see cref="DashboardResponse.PendingQuestionnaire"/> —
    /// <see cref="OnQaTapped"/> is what a caregiver taps into to answer it.
    /// </summary>
    /// <remarks>
    /// The one news button that outlives being opened: a look is not an answer, and answering or
    /// dismissing the question is what takes it away. So it keeps the old two states — its glyph
    /// coloured until this exact question has been opened, plain after — where the others simply
    /// go once read.
    /// </remarks>
    private void ApplyPendingQuestionnaire(QuestionnaireResponse? pending)
    {
        _pendingQuestionId = pending?.Id;

        if (pending is null)
            return;

        SetQaGlyph(AttentionMarks.IsQuestionUnread(_memberId, pending.Id));

        // Always "1" today — at most one pending question per member (see
        // QuestionnairesPageResponse.Pending) — but a superscript number rather than a dot, so
        // this still reads correctly if that ever stops being true.
        QaBadgeLabel.Text = "1";
        SemanticProperties.SetDescription(QaCluster, "Questions, 1 waiting");
    }

    private void SetQaGlyph(bool unread) =>
        QaIcon.Source = unread ? "icon_tab_qa_primary.svg" : "icon_tab_qa.svg";

    /// <summary>
    /// The Advise button, from <see cref="DashboardResponse.HasAdvise"/> — on the line while the
    /// suggestion on offer is newer than the last one this caregiver opened; a regeneration is
    /// what makes it new again (<see cref="AttentionMarks"/>). Once read, the suggestion is still
    /// on Details' "Something to try" card; with none on offer, that card is hidden too.
    /// </summary>
    private void ApplyAdvise(bool hasAdvise, DateTime? generatedAtUtc)
    {
        _adviseGeneratedAt = generatedAtUtc;
        _adviseUnread = hasAdvise
            && AttentionMarks.IsUnread(AttentionMarks.Advise, _memberId, generatedAtUtc);
    }

    /// <summary>
    /// The CardiJournal button, from <see cref="DashboardResponse.LatestJournalEntryAt"/> — on the
    /// line until the caregiver has opened an entry at least as new as the latest one
    /// (<see cref="AttentionMarks"/>). The journal itself is always a tab away.
    /// </summary>
    private void ApplyDaybook(DateTime? latestEntryAtUtc)
    {
        _latestJournalEntryAt = latestEntryAtUtc;
        _journalUnread = AttentionMarks.IsUnread(AttentionMarks.Journal, _memberId, latestEntryAtUtc);
    }

    /// <summary>
    /// The Alerts button, while this CardiMember has an alert nobody has acknowledged —
    /// <see cref="DashboardResponse.UnreadAlertCount"/>, which the server counts as the unresolved
    /// alerts with no <c>AcknowledgedDate</c>, the same set the Recent Alerts strip below shows and
    /// the header bell's badge counts.
    /// </summary>
    /// <remarks>
    /// Acknowledging is what takes the button away, not resolving. It is a request for attention,
    /// and acknowledging is the caregiver answering it; an episode they have already answered must
    /// not keep asking, even while it stays open on the alerts list and keeps the card's own status
    /// colour. That wider unresolved set is still what <see cref="DashboardResponse.OpenAlertCount"/>
    /// carries, for the readers that want it. Opening the list is not acknowledging, so a tap
    /// leaves the button where it is.
    /// </remarks>
    private void ApplyOpenAlerts(int unreadCount)
    {
        _unreadAlertCount = unreadCount;

        // The count lives on the header's badge, not here — but a screen reader cannot count
        // dots, so this is the only place it can hear it.
        SemanticProperties.SetDescription(AlertsCluster, unreadCount switch
        {
            <= 0 => "Alerts",
            1 => "Alerts, 1 unread",
            _ => $"Alerts, {unreadCount} unread",
        });
    }

    private void OnDaybookTapped(object? sender, TappedEventArgs e)
    {
        // Opening it is reading it: the button goes now rather than on the next reload, so the
        // caregiver sees it answer their tap before the page changes under it.
        AttentionMarks.MarkSeen(AttentionMarks.Journal, _memberId, _latestJournalEntryAt);
        _journalUnread = false;
        ShowNews();
        DaybookRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAlertsTapped(object? sender, TappedEventArgs e) =>
        AlertsRequested?.Invoke(this, EventArgs.Empty);

    private void OnNoDeviceTapped(object? sender, TappedEventArgs e) =>
        NoDeviceRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The page decides what "answer it" means — see
    /// <see cref="IPopupService.ShowPendingQuestionAsync"/>.
    /// </summary>
    private void OnQaTapped(object? sender, TappedEventArgs e)
    {
        if (_pendingQuestionId is { } questionId)
        {
            AttentionMarks.MarkQuestionSeen(_memberId, questionId);
            SetQaGlyph(false);
        }
        QuestionRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The page decides the journey — see <see cref="CardiMemberDetailPage.AdviseFocus"/>.
    /// </summary>
    private void OnAdviseTapped(object? sender, TappedEventArgs e)
    {
        AttentionMarks.MarkSeen(AttentionMarks.Advise, _memberId, _adviseGeneratedAt);
        _adviseUnread = false;
        ShowNews();
        AdviseRequested?.Invoke(this, EventArgs.Empty);
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

        // With no device, the line stops reporting the gap and says how to close it: "Not synced
        // yet" in red told a caregiver something was wrong without saying what to do, and the
        // no-device button beside Alerts is a small glyph to find. The link opens the same offer.
        _connectLinkShown = freshnessRelevant && !data.Device.HasActiveConnection;
        LastUpdatedFooterLabel.IsVisible = showAge || _connectLinkShown;
        if (_connectLinkShown)
        {
            LastUpdatedFooterLabel.Text = "Connect a device ›";
            LastUpdatedFooterLabel.TextColor = MetricStatus.Resource("Primary", Colors.Blue);
            SemanticProperties.SetDescription(LastUpdatedFooterLabel, $"{data.DataFreshnessMessage}. Connect a device");
            SemanticProperties.SetHint(LastUpdatedFooterLabel, "Double tap to connect a device");
        }
        else
        {
            LastUpdatedFooterLabel.Text = data.LastSyncedAt is { } lastSynced
                ? $"Updated {RelativeTime.Format(lastSynced)}"
                : "Not synced yet";
            LastUpdatedFooterLabel.TextColor = freshnessColor;
            SemanticProperties.SetDescription(
                LastUpdatedFooterLabel, $"{data.DataFreshnessMessage}. {LastUpdatedFooterLabel.Text}");
            SemanticProperties.SetHint(LastUpdatedFooterLabel, string.Empty);
        }

        // Exactly one node announces the freshness state, and only while there is a state worth
        // announcing — the label whenever it is on screen, the block only while the learning bar
        // alone keeps it there.
        SemanticProperties.SetDescription(
            FreshnessBlock, LastUpdatedFooterLabel.IsVisible ? string.Empty : data.DataFreshnessMessage);

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
            ShowMetricsControls();
            return;
        }

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
        ShowMetricsControls();
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
