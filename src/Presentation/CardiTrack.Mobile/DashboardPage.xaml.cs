using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Navigation;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Onboarding;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class DashboardPage : ContentPage
{
    /// <summary>Also cleared by M1-13 when the remembered member is removed.</summary>
    internal const string PrimaryMemberIdKey = "PrimaryCardiMemberId";

    /// <summary>The member the dashboard currently shows — what the Daybook link filters to.</summary>
    private Guid _memberId;
    private const string VerifyEmailDismissedKey = "VerifyEmailNudgeDismissed";
    private const string DismissedSleepAlertKey = "DismissedSleepAlertId";
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(2);

    /// <summary>
    /// How long the hero card waits for the live status line before it admits to waiting — see
    /// <see cref="LoadCurrentStatusAsync"/>. Long enough that a cached answer never flashes a
    /// placeholder, short enough that a generation isn't left looking like a finished screen.
    /// </summary>
    private static readonly TimeSpan StatusLoadingThreshold = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How old a stored status line may be and still be put back on the card instead of the
    /// loading placeholder — see <see cref="RestoreStatusLineAsync"/>. Wide enough to cover a
    /// caregiver reopening the app through the day, short enough that what they read is still
    /// about today.
    /// </summary>
    private static readonly TimeSpan StatusLineRestoreWindow = TimeSpan.FromHours(6);

    /// <summary>Columns in the Key Metrics grid; see <see cref="LayoutMetricCards"/>.</summary>
    private const int MetricsPerRow = 2;

    private readonly ICardiTrackApiClient _api;
    private readonly IAuthService _authService;
    private readonly IPopupService _popups;
    private readonly IStatusLineStore _statusLines;

    private enum DashboardState { Loading, Loaded, NoMember, Error }

    private bool _isLoading;
    private bool _isSyncing;
    private bool _wizardActive;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private DashboardResponse? _lastData;
    private Guid? _currentSleepAlertId;

    public DashboardPage(
        ICardiTrackApiClient api,
        IAuthService authService,
        IPopupService popups,
        IStatusLineStore statusLines)
    {
        InitializeComponent();
        _api = api;
        _authService = authService;
        _popups = popups;
        _statusLines = statusLines;
        HeroCard.MemberTapped += (_, _) => OpenMemberDetails();
        HeroCard.WeatherTapped += async (_, weather) => await _popups.ShowWeatherAsync(weather);
        Header.RefreshRequested += OnRefreshClicked;
        Header.BellTapped += OnBellClicked;

        this.RefreshWhenAppResumes(RefreshUnattendedAsync);

        // A monitoring screen left open has to keep itself current. Until this, every refresh in
        // the app was edge-triggered — a caregiver watching the dashboard saw nothing move until
        // they pulled it down themselves.
        this.RefreshEvery(PeriodicRefresh.LiveDataInterval, RefreshUnattendedAsync);

        TabNavigation.DashboardExitArmed += OnDashboardExitArmed;
        Unloaded += (_, _) =>
        {
            TabNavigation.DashboardExitArmed -= OnDashboardExitArmed;
            _exitHintCts?.Cancel();
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        UpdateGreeting();
        UpdateVerifyEmailBanner();

        // Arriving on the screen is a pull, like the tick and the resume. This used to skip the
        // load when the last one was under a couple of minutes old, which meant a caregiver who
        // came here deliberately — the one moment they are certainly asking "how are they now?" —
        // could be shown a screen up to two minutes stale and no request in flight. The only gate
        // left is the shared MinimumGap floor, which exists to stop a load that has just run being
        // repeated: Android raises OnAppearing again on its way back to the foreground, where iOS
        // does not, so without it a resume would fetch twice on one platform and once on the other.
        _ = RefreshUnattendedAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        HideExitHint();
        TabNavigation.DisarmDashboardExit();
    }

    private CancellationTokenSource? _exitHintCts;

    private void OnDashboardExitArmed(object? sender, EventArgs e)
    {
        ExitHintBanner.IsVisible = true;
        _exitHintCts?.Cancel();
        _exitHintCts = new CancellationTokenSource();
        var ct = _exitHintCts.Token;
        _ = HideExitHintAfterAsync(ct);
    }

    private async Task HideExitHintAfterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(ExitConfirmation.Window, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        HideExitHint();
    }

    private void HideExitHint()
    {
        _exitHintCts?.Cancel();
        ExitHintBanner.IsVisible = false;
    }

    // Soft email-verification capture: nudge only, never a gate. Claim comes from the
    // ID token, so it clears on the first launch after the user taps Auth0's link.
    private void UpdateVerifyEmailBanner()
    {
        var show = _authService.IsEmailVerified == false
            && !Preferences.Default.Get(VerifyEmailDismissedKey, false);
        if (show)
            VerifyEmailLabel.Text = string.IsNullOrWhiteSpace(_authService.CurrentUserEmail)
                ? "Verify your email — check your inbox for the confirmation link."
                : $"Verify your email — we sent a link to {_authService.CurrentUserEmail}.";
        VerifyEmailBanner.IsVisible = show;
    }

    private void OnDismissVerifyEmailClicked(object? sender, EventArgs e)
    {
        Preferences.Default.Set(VerifyEmailDismissedKey, true);
        VerifyEmailBanner.IsVisible = false;
    }

    private void OnDismissSleepConcernClicked(object? sender, EventArgs e)
    {
        if (_currentSleepAlertId is { } id)
            Preferences.Default.Set(DismissedSleepAlertKey, id.ToString());
        SleepConcernBanner.IsVisible = false;
    }

    private async void OnSleepConcernTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToTabAsync(AppShell.AlertsRoute);

    /// <summary>
    /// Short, quiet time-of-day line under the caregiver's own name — describes the caregiver's
    /// local evening, not the CardiMember's, so there's no cross-timezone reading to get wrong.
    /// Deliberately time-only: no weather/location signal exists anywhere in this app yet, so a
    /// "based on temperature" version is a separate, later feature, not a copy change here.
    /// </summary>
    private static string ContextLineFor(int hour) => hour switch
    {
        < 5 => "Hope you're getting some rest",
        < 8 => "Rise and shine",
        < 12 => "Hope your morning's off to a good start",
        < 17 => "Hope your afternoon is going well",
        < 21 => "Seems like a nice evening",
        _ => "Winding down for the night",
    };

    private void UpdateGreeting()
    {
        var timeOfDay = DateTime.Now.Hour switch
        {
            < 12 => "Good Morning",
            < 18 => "Good Afternoon",
            _ => "Good Evening",
        };
        var firstName = _authService.CurrentUserName?.Split(' ')[0];
        Header.SetGreeting(
            string.IsNullOrWhiteSpace(firstName) ? timeOfDay : firstName,
            ContextLineFor(DateTime.Now.Hour));
    }

    /// <summary>
    /// Color token for each <see cref="DashboardResponse.DataFreshness"/> tier. An
    /// unrecognised or empty value falls back to the neutral "unknown" color, not green — an
    /// unexpected value showing a reassuring color would be worse than showing none.
    /// </summary>
    private static string FreshnessColorKey(string tier) => tier switch
    {
        "red" => "StatusRed",
        "amber" => "StatusYellow",
        "blue" => "StatusBlue",
        "green" => "StatusGreen",
        _ => "StatusUnknown",
    };

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        // SyncAndReloadAsync raises this itself when it drives the spinner from a button tap.
        // Bailing out leaves that run to finish rather than starting a second one.
        if (_isSyncing)
            return;
        await SyncAndReloadAsync();
    }

    private void OnRefreshClicked(object? sender, EventArgs e) => _ = SyncAndReloadAsync();

    /// <summary>
    /// The quiet reload behind all three unattended paths — arriving on the screen, the app
    /// returning to the foreground, and the timer ticking while the caregiver watches. All three
    /// share one floor, <see cref="ResumeRefresh.MinimumGap"/>, and nothing else: any longer
    /// window would hold back the very update the caregiver came to see.
    /// </summary>
    /// <remarks>
    /// A read, not a device sync: the server has been collecting from the wearable on its own —
    /// webhook-triggered within seconds, with the Worker's ten-minute poll as the fallback — so
    /// what is missing on screen is the fetch, not the collection. Asking the server to check in
    /// with the device on every foreground or tick would also earn the "too soon since the last
    /// check" refusal, and with it a popup for something nobody asked for. Only a deliberate pull
    /// or the refresh button syncs the device.
    /// </remarks>
    private Task RefreshUnattendedAsync() =>
        DateTime.UtcNow - _lastLoadedUtc < ResumeRefresh.MinimumGap
            ? Task.CompletedTask
            : LoadAsync(force: false);

    /// <summary>
    /// Asks the server to pull from the wearable now, then reloads (issue #67).
    /// </summary>
    /// <remarks>
    /// Refresh used to re-read only what the scheduled worker had already stored, so a member
    /// whose sync hadn't run yet sat on "Not synced yet" however often you tapped. The reload
    /// runs even when the sync is refused or fails, so a screen that is merely stale still
    /// catches up. The refusal is reported afterwards rather than inline: a popup awaited mid-run
    /// would hold the spinner up behind it.
    /// </remarks>
    private async Task SyncAndReloadAsync()
    {
        if (_isSyncing)
            return;
        _isSyncing = true;
        Header.IsRefreshEnabled = false;
        Refresher.IsRefreshing = true;

        string? syncError = null;
        try
        {
            if (_lastData is { } data)
            {
                try
                {
                    await _api.SyncDevicesAsync(data.CardiMemberId);
                }
                catch (ApiException ex)
                {
                    // Paused monitoring, no connected device, or too soon since the last check —
                    // each is the answer to "why hasn't this updated?", so none stays silent.
                    syncError = ex.Message;
                }
            }

            await LoadAsync(force: true);
        }
        finally
        {
            Refresher.IsRefreshing = false;
            Header.IsRefreshEnabled = true;
            _isSyncing = false;
        }

        if (syncError is not null && !_api.LastGetWasCached)
            await _popups.ShowInfoAsync(syncError, "Couldn't check in");
    }

    private async Task LoadAsync(bool force)
    {
        if (_isLoading)
            return;
        _isLoading = true;

        if (_lastData is null)
            SetState(DashboardState.Loading);

        try
        {
            var memberId = await ResolveMemberIdAsync(force);
            if (memberId is null)
            {
                SetState(DashboardState.NoMember);
                return;
            }

            var data = await _api.GetDashboardAsync(memberId.Value);
            Apply(data);

            // Committed only once it is actually on screen. Both catches below read _lastData as
            // "there is already a dashboard here worth keeping", which is only true after Apply
            // has run: assigning it first meant a fault part-way through Apply left the field set
            // over a screen that had never been filled in, and the error paths then protected a
            // skeleton instead of replacing it.
            _lastData = data;
            _lastLoadedUtc = DateTime.UtcNow;
            SetState(DashboardState.Loaded);

            // Fire-and-forget, not awaited: the hero card already shows its static per-tier
            // copy, and a MedGemma call can take a few seconds — nothing about the dashboard
            // should wait on it, including the pull-to-refresh spinner below.
            _ = LoadCurrentStatusAsync(data);

            // Loaded after the dashboard rather than alongside it: a caregiver opens this screen
            // to see how their relative is, and housekeeping must never delay that answer or take
            // it down with it.
            await LoadNudgesAsync();
        }
        catch (ApiException ex)
        {
            if (_lastData is null)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(DashboardState.Error);
            }
            // With data already on screen, keep it — pull-to-refresh failing quietly
            // beats blanking the dashboard.
        }
        catch (Exception ex)
        {
            // Anything that is not the API answering badly — a fault while putting the data on
            // screen, most likely. This catch exists because without it such a fault is silent and
            // permanent: it escapes into a fire-and-forget task with nothing observing it, the page
            // never reaches Loaded, and every retry meets the same data and fails the same way, so
            // the caregiver is left watching loading placeholders for the rest of the session with
            // nothing to tap. A monitoring screen may fail, but it has to admit that it failed.
            ScreenRefresh.LogFailure(ex, this, "while loading");
            if (_lastData is null)
            {
                ErrorDetailLabel.Text = "Something went wrong while showing this dashboard.";
                SetState(DashboardState.Error);
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task<Guid?> ResolveMemberIdAsync(bool force)
    {
        var cached = Preferences.Default.Get(PrimaryMemberIdKey, string.Empty);
        if (!force && Guid.TryParse(cached, out var cachedId))
            return cachedId;

        var primary = PrimaryCardiMember.From(await _api.GetCardiMembersAsync());
        if (primary is null)
        {
            Preferences.Default.Remove(PrimaryMemberIdKey);
            return null;
        }

        Preferences.Default.Set(PrimaryMemberIdKey, primary.Id.ToString());
        return primary.Id;
    }

    private void Apply(DashboardResponse data)
    {
        _memberId = data.CardiMemberId;
        HeroCard.Apply(data);

        Header.SetUnreadCount(data.UnreadAlertCount);

        var firstName = NameFormatting.FirstName(data.Name);
        QuickActions.Apply(
            new QuickActionTarget(
                data.CardiMemberId,
                data.Name,
                data.Phone,
                data.EmergencyContactPhone,
                data.EmergencyContactName),
            _popups);

        // Paused banner (M1-13)
        PausedBanner.IsVisible = data.MonitoringPaused;
        if (data.MonitoringPaused)
        {
            var until = data.MonitoringPausedUntil is { } pausedUntil
                ? DateTime.SpecifyKind(pausedUntil, DateTimeKind.Utc).ToLocalTime().ToString("MMM d, h:mm tt")
                : "further notice";
            PausedBannerLabel.Text = $"Monitoring is paused until {until} — we're not collecting data or raising alerts.";
        }

        OfflineBanner.ApplyFrom(_api);

        // Stale banner (M1-09c). Suppressed while paused: data is meant to be stale then,
        // and "pull down to check in" would be advice we can't honour. Also suppressed while
        // offline — the offline banner already says the data is last-known-good.
        var isStale = !data.MonitoringPaused
            && !OfflineBanner.IsVisible
            && data.LastSyncedAt is { } synced
            && DateTime.UtcNow - DateTime.SpecifyKind(synced, DateTimeKind.Utc) > StaleThreshold;
        var wasStale = StaleBanner.IsVisible;
        StaleBanner.IsVisible = isStale;
        if (isStale)
        {
            StaleBannerLabel.Text =
                $"Last update was {RelativeTime.Format(data.LastSyncedAt!.Value)} — pull down to check in";

            // Only fade on the transition into "stale" — re-applying the same state on every
            // 5-minute auto-refresh would otherwise re-fade a banner that's already visible.
            // Already-stale still forces full opacity rather than leaving it untouched: a fade
            // interrupted by the app backgrounding mid-animation would otherwise strand the
            // banner semi-transparent until it leaves and re-enters the stale state.
            if (!wasStale)
            {
                StaleBanner.Opacity = 0;
                _ = StaleBanner.FadeToAsync(1, 180, Easing.CubicOut);
            }
            else
            {
                StaleBanner.Opacity = 1;
            }
        }
        else
        {
            StaleBanner.Opacity = 0;
        }

        // No device (M1-09d)
        NoDeviceCard.IsVisible = !data.Device.HasActiveConnection;
        NoDeviceLabel.Text = $"Connect {firstName}'s device so CardiTrack can start watching over them";

        // Data-pipeline freshness (deterministic — see MemberInsightsCalculator). Suppressed
        // while paused, same rule the stale banner applies — collection is intentionally
        // stopped, so a freshness reading here would misreport a deliberate pause as a gap.
        FreshnessBlock.IsVisible = !data.MonitoringPaused;
        var freshnessColor = (Color)Microsoft.Maui.Controls.Application.Current!.Resources[FreshnessColorKey(data.DataFreshness)];
        LastUpdatedFooterLabel.Text = data.LastSyncedAt is { } lastSynced
            ? $"Updated {RelativeTime.Format(lastSynced)}"
            : "Not synced yet";
        // The age line carries the freshness state now that the message above it is gone: colour
        // for the eye, the message itself for a screen reader, which cannot read a colour.
        LastUpdatedFooterLabel.TextColor = freshnessColor;
        SemanticProperties.SetDescription(
            LastUpdatedFooterLabel, $"{data.DataFreshnessMessage}. {LastUpdatedFooterLabel.Text}");

        // Baseline-learning progress only while the window is still running — a permanently
        // full bar after it completes would say nothing new every day.
        LearningProgress.IsVisible = data.Baseline.IsLearning && data.Device.HasActiveConnection
            && !data.MonitoringPaused;
        LearningProgress.Progress = data.Baseline.PercentComplete / 100.0;
        LearningProgress.ProgressColor = freshnessColor;

        // Poor-sleep nudge: points at the real, unacknowledged Sleep alert StatisticalAlertWorker
        // already raises, rather than a second judgement derived from today's metric alone.
        var sleepAlert = data.RecentAlerts.FirstOrDefault(a => a.Type == "Sleep" && a.Status == "new");
        var dismissedId = Preferences.Default.Get(DismissedSleepAlertKey, string.Empty);
        _currentSleepAlertId = sleepAlert?.AlertId;
        SleepConcernBanner.IsVisible = sleepAlert is not null
            && sleepAlert.AlertId.ToString() != dismissedId;
        if (SleepConcernBanner.IsVisible)
            SleepConcernBannerLabel.Text = $"{firstName}'s sleep has looked different than usual lately. Tap to view.";

        // Metrics
        if (data.Metrics is { } metrics)
        {
            MetricsAccordion.IsVisible = true;
            StepsCard.ApplySteps(metrics.Steps);
            HeartRateCard.ApplyHeartRate(metrics.RestingHeartRate);
            SleepCard.ApplySleep(metrics.Sleep);

            // Not every connected device reports these, so the row disappears entirely rather
            // than showing a permanent "—" for a member whose wearable never will.
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
        else
        {
            MetricsAccordion.IsVisible = false;
        }

        // Recent alerts
        AlertsStack.Clear();
        AlertsSection.IsVisible = data.RecentAlerts.Count > 0;
        foreach (var alert in data.RecentAlerts)
        {
            var card = new AlertMiniCard();
            card.Apply(alert);
            card.AlertTapped += OnAlertTapped;
            AlertsStack.Add(card);
        }
    }

    /// <summary>
    /// Packs the Key Metrics tiles two to a row in reading order, skipping the ones this
    /// member's wearable doesn't report.
    /// </summary>
    /// <remarks>
    /// The order leads with the night — resting heart rate beside sleep — then the day, skin
    /// temperature beside activity, and closes with the two readings a device reports without any
    /// comparison to make. Positions are assigned here rather than pinned in XAML because three of
    /// the six tiles are optional — skin temperature, SpO2 and breathing rate each depend on what
    /// the device sends — and a fixed slot for an absent tile leaves its partner sitting alone
    /// beside a half-row of nothing, which reads as a bug rather than as an absence. Packing keeps
    /// the grid solid whatever the device reports; the declared pairing is what a member whose
    /// device reports everything sees.
    /// </remarks>
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

    private void SetState(DashboardState state)
    {
        SkeletonPanel.IsVisible = state == DashboardState.Loading;
        ContentPanel.IsVisible = state == DashboardState.Loaded;
        NoMemberPanel.IsVisible = state == DashboardState.NoMember;
        ErrorPanel.IsVisible = state == DashboardState.Error;
    }

    /// <summary>
    /// Both the hero card and the quick-action row's Details tile land on M1-13. The member id
    /// comes from the loaded dashboard rather than the cached preference, so it always matches
    /// whoever is actually on screen.
    /// </summary>
    private void OpenMemberDetails()
    {
        if (_lastData is not { } data)
            return;
        _ = Shell.Current.GoToAsync($"{CardiMemberDetailPage.Route}?memberId={data.CardiMemberId}");
    }

    /// <summary>
    /// The bell is the way in to everything wanting attention. It opens the alerts list, which
    /// carries completeness items in their own section below the health ones — sectioned, never
    /// interleaved, so scanning for a health event does not mean wading through housekeeping.
    /// </summary>
    private async void OnBellClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToTabAsync(AppShell.AlertsRoute);

    /// <summary>
    /// Lands on M1-10, which is still a placeholder. The link renders anyway: hiding it would
    /// make the screen diverge from the design for a reason no caregiver can see, and the
    /// placeholder is the more honest signal that alerting is not finished.
    /// </summary>
    private async void OnViewAllAlertsTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToTabAsync(AppShell.AlertsRoute);

    /// <summary>The member card's link to their finished days — the Daybook tab, filtered to
    /// them, arriving through the same origin-remembering jump every content affordance uses.</summary>
    private async void OnDaybookTapped(object? sender, TappedEventArgs e)
    {
        if (_memberId == Guid.Empty)
            return;

        await Shell.Current.GoToTabAsync($"{AppShell.DaybookRoute}?memberId={_memberId}");
    }

    /// <summary>A dashboard recent-alert tile opens the matching detail screen.</summary>
    private async void OnAlertTapped(object? sender, Guid alertId)
    {
        if (alertId == Guid.Empty)
            return;
        await Shell.Current.GoToAsync($"{AlertDetailPage.Route}?alertId={alertId}");
    }

    private async void OnAddMemberClicked(object? sender, EventArgs e)
    {
        if (_wizardActive)
            return;
        _wizardActive = true;
        try
        {
            await WizardLauncher.RunModalAsync(Navigation, member: null);
            // Bypass the auto-refresh window and re-resolve the primary member —
            // this may have been the first one.
            await LoadAsync(force: true);
        }
        catch (Exception ex)
        {
            // async void: anything escaping here takes the app down rather than
            // reaching a caller. RunModalAsync rethrows when the modal can't be
            // pushed, so the dashboard has to absorb it and stay usable.
            await _popups.ShowErrorAsync(ex.Message, "Couldn't add a CardiMember");
        }
        finally
        {
            _wizardActive = false;
        }
    }

    private async void OnConnectDeviceClicked(object? sender, EventArgs e)
    {
        if (_wizardActive)
            return;
        _wizardActive = true;
        try
        {
            // One round trip: the members list answers both "which member" and "is there one".
            var cached = Preferences.Default.Get(PrimaryMemberIdKey, string.Empty);
            var member = PrimaryCardiMember.From(
                await _api.GetCardiMembersAsync(),
                Guid.TryParse(cached, out var cachedId) ? cachedId : null);
            if (member is null)
                return;

            await WizardLauncher.RunModalAsync(Navigation, member);
            await LoadAsync(force: true);
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't start device setup");
        }
        catch (ApiException)
        {
            // An expired session is already taking the user back to sign-in — a popup
            // here would only land on top of that page explaining nothing.
        }
        catch (Exception ex)
        {
            // ApiException covers the members fetch above, but a failed modal push
            // arrives as something else — and this is async void too.
            await _popups.ShowErrorAsync(ex.Message, "Couldn't start device setup");
        }
        finally
        {
            _wizardActive = false;
        }
    }

    /// <summary>
    /// A live, empathetic replacement for the hero card's static status line. Best-effort: the
    /// static copy <see cref="Apply"/> already rendered is a complete, correct fallback, and every
    /// path that does not produce a live line puts it back.
    /// </summary>
    private async Task LoadCurrentStatusAsync(DashboardResponse data)
    {
        // Neither tier calls the model: a paused member has no reading to interpret, and one with
        // no baseline yet already shows the day's own numbers. Returning here is what keeps them
        // off the loading line below, which they would otherwise never leave.
        if (data.HealthStatus is "unknown" or "paused")
            return;

        var pending = _api.GetCurrentStatusAsync(data.CardiMemberId);

        // Put back the line this member last had before deciding whether to admit to waiting.
        // The card's live line lives in fields on the control, so it dies with the page — and the
        // page is transient behind a tab template, which made every cold start look like a first
        // load and sent a caregiver reopening the app to the placeholder even though the answer
        // was already on the device. Restoring first means the gate below sees a live line and
        // leaves it alone; the refresh already in flight replaces it in place a moment later.
        if (!HeroCard.HasLiveStatusFor(data.CardiMemberId, data.HealthStatus))
            await RestoreStatusLineAsync(data);

        // Only say "Loading" once the wait is long enough to be worth admitting to.
        //
        // This used to blank the card the moment the call started, on the reasoning that showing
        // the per-tier copy as though it were the answer and swapping it under the reader a few
        // seconds later was the worse of the two. That reasoning holds for a slow call and not for
        // a quick one, and the quick one is the ordinary case — the server caches this line for
        // minutes, so most loads answer from cache in well under a second. What the unconditional
        // version cost was the cold path: the generation runs to a 25-second server budget, and a
        // caregiver opening the dashboard could sit on "Please wait — checking how they're doing"
        // for all of it, with the tier's own perfectly good sentence withheld the whole time.
        //
        // Waiting the threshold gets both: a cached answer goes straight from the static line to
        // the live one with no placeholder in between, and a generation still says what it is
        // doing rather than leaving a stale-looking sentence to be replaced without warning.
        //
        // Skipped when the card already shows a live line for this member and tier — an unattended
        // tick would otherwise blank a good line to re-fetch the same words.
        if (!HeroCard.HasLiveStatusFor(data.CardiMemberId, data.HealthStatus)
            && await Task.WhenAny(pending, Task.Delay(StatusLoadingThreshold)) != pending)
        {
            HeroCard.ShowStatusLoading();
        }

        try
        {
            var status = await pending;
            if (status.Message is { } message)
            {
                HeroCard.ApplyDynamicMessage(
                    status.Headline, message, data.CardiMemberId, data.HealthStatus);

                // Kept with the tier it describes, so the next cold start can tell whether it is
                // still about the day on screen.
                await _statusLines.SaveAsync(
                    data.CardiMemberId,
                    new StoredStatusLine(data.HealthStatus, status.Headline, message, status.GeneratedAt));
            }
            else
            {
                // Nothing to say after all — back to the tier's own copy, and forget any live
                // line first so the re-apply doesn't just restore the one we were told is gone.
                // That includes the stored copy: leaving it would put a sentence the server has
                // just retired back on the card at the next launch.
                await _statusLines.ClearAsync(data.CardiMemberId);
                HeroCard.ClearLiveStatus();
                HeroCard.Apply(data);
            }
        }
        catch (ApiException)
        {
            // Put the static copy back: the card may be showing "Loading", and leaving it there
            // would turn a failed side-call into a screen that never resolves. Harmless when it
            // isn't — Apply re-renders the same tier, and restores the live line if one survived,
            // which now includes a line restored from the device a moment ago.
            HeroCard.Apply(data);
            // Static per-tier copy stays. Nothing to show the caregiver about this failure —
            // it isn't actionable and isn't worth interrupting them for.
        }
    }

    /// <summary>
    /// Shows the last status line saved for this member, if one is recent enough and was written
    /// about the tier now on screen. Best-effort in every direction: no stored line, a stale one,
    /// or a store that cannot be read all leave the card exactly as <see cref="Apply"/> rendered it.
    /// </summary>
    private async Task RestoreStatusLineAsync(DashboardResponse data)
    {
        StoredStatusLine? stored;
        try
        {
            stored = await _statusLines.TryGetAsync(
                data.CardiMemberId, data.HealthStatus, StatusLineRestoreWindow);
        }
        catch (Exception ex)
        {
            // The store swallows its own I/O failures; this is the belt-and-braces catch for
            // anything it doesn't. A dashboard must not fail over a cosmetic read.
            ScreenRefresh.LogFailure(ex, this, "while restoring the saved status line");
            return;
        }

        if (stored is not null)
        {
            HeroCard.ApplyDynamicMessage(
                stored.Headline, stored.Message, data.CardiMemberId, data.HealthStatus);
        }
    }

    // ------------------------------------------------------------------ data-completeness nudges

    /// <summary>
    /// Fills the safety banners and the two "Complete the picture" slots.
    /// </summary>
    /// <remarks>
    /// Failures are swallowed on purpose. This is the housekeeping strip on a health screen — if
    /// the summary call fails, the right outcome is a dashboard without it, not an error dialog
    /// over the metrics somebody actually came to read.
    /// </remarks>
    private async Task LoadNudgesAsync()
    {
        try
        {
            var summary = await _api.GetNotificationSummaryAsync();
            RenderNudges(summary);
        }
        catch (ApiException)
        {
            SafetyBannerList.IsVisible = false;
            CompleteThePictureCard.IsVisible = false;
            Header.SetNudgeIndicator(false);
        }
    }

    private void RenderNudges(NotificationSummaryResponse summary)
    {
        SafetyBannerList.Clear();
        NudgeList.Clear();

        foreach (var banner in summary.SafetyBanners)
        {
            var row = new NudgeMiniRow(banner, asSafetyBanner: true);
            row.Tapped += OnNudgeTapped;
            SafetyBannerList.Add(row);
        }

        foreach (var card in summary.DashboardCards)
        {
            var row = new NudgeMiniRow(card);
            row.Tapped += OnNudgeTapped;
            NudgeList.Add(row);
        }

        SafetyBannerList.IsVisible = summary.SafetyBanners.Count > 0;
        CompleteThePictureCard.IsVisible = summary.DashboardCards.Count > 0;
        Header.SetNudgeIndicator(summary.OpenCount > 0);

        // The link is only worth offering when there is more behind it than the two on screen.
        CompleteThePictureLink.IsVisible = summary.OpenCount > summary.DashboardCards.Count;
    }

    private async void OnNudgeTapped(object? sender, NotificationResponse notification) =>
        await Shell.Current.GoToAsync(NotificationsPage.Route);

    private async void OnSeeAllNudgesTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(NotificationsPage.Route);
}
