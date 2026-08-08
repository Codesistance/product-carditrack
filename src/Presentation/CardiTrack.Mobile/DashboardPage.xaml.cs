using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Onboarding;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class DashboardPage : ContentPage
{
    /// <summary>Also cleared by M1-13 when the remembered member is removed.</summary>
    internal const string PrimaryMemberIdKey = "PrimaryCardiMemberId";
    private const string VerifyEmailDismissedKey = "VerifyEmailNudgeDismissed";
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(2);
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMinutes(5);
    private const double UnavailableActionOpacity = 0.4;

    private readonly ICardiTrackApiClient _api;
    private readonly IAuthService _authService;
    private readonly IPopupService _popups;

    private enum DashboardState { Loading, Loaded, NoMember, Error }

    private bool _isLoading;
    private bool _isSyncing;
    private bool _wizardActive;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private DashboardResponse? _lastData;

    public DashboardPage(ICardiTrackApiClient api, IAuthService authService, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _authService = authService;
        _popups = popups;
        HeroCard.SyncRequested += (_, _) => _ = SyncAndReloadAsync();
        HeroCard.MemberTapped += (_, _) => OpenMemberDetails();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        UpdateGreeting();
        UpdateVerifyEmailBanner();
        if (_lastData is null || DateTime.UtcNow - _lastLoadedUtc > AutoRefreshInterval)
            _ = LoadAsync(force: false);
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

    private void UpdateGreeting()
    {
        var timeOfDay = DateTime.Now.Hour switch
        {
            < 12 => "Good Morning",
            < 18 => "Good Afternoon",
            _ => "Good Evening",
        };
        var firstName = _authService.CurrentUserName?.Split(' ')[0];
        GreetingLabel.Text = string.IsNullOrWhiteSpace(firstName)
            ? timeOfDay
            : $"{timeOfDay}, {firstName}";
    }

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
        RefreshButton.IsEnabled = false;
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
            RefreshButton.IsEnabled = true;
            _isSyncing = false;
        }

        if (syncError is not null)
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
            _lastData = data;
            _lastLoadedUtc = DateTime.UtcNow;
            Apply(data);
            SetState(DashboardState.Loaded);
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
        HeroCard.Apply(data);

        BellBadge.IsVisible = data.UnreadAlertCount > 0;
        BellBadgeLabel.Text = data.UnreadAlertCount > 9 ? "9+" : data.UnreadAlertCount.ToString();

        var firstName = NameFormatting.FirstName(data.Name);
        CallLabel.Text = $"Call {firstName}";
        ApplyPhoneAvailability(data, firstName);

        // Paused banner (M1-13)
        PausedBanner.IsVisible = data.MonitoringPaused;
        if (data.MonitoringPaused)
        {
            var until = data.MonitoringPausedUntil is { } pausedUntil
                ? DateTime.SpecifyKind(pausedUntil, DateTimeKind.Utc).ToLocalTime().ToString("MMM d, h:mm tt")
                : "further notice";
            PausedBannerLabel.Text = $"Monitoring is paused until {until} — we're not collecting data or raising alerts.";
        }

        // Stale banner (M1-09c). Suppressed while paused: data is meant to be stale then,
        // and "pull down to check in" would be advice we can't honour.
        var isStale = !data.MonitoringPaused
            && data.LastSyncedAt is { } synced
            && DateTime.UtcNow - DateTime.SpecifyKind(synced, DateTimeKind.Utc) > StaleThreshold;
        StaleBanner.IsVisible = isStale;
        if (isStale)
            StaleBannerLabel.Text =
                $"Last update was {RelativeTime.Format(data.LastSyncedAt!.Value)} — pull down to check in";

        // No device (M1-09d)
        NoDeviceCard.IsVisible = !data.Device.HasActiveConnection;
        NoDeviceLabel.Text = $"Connect {firstName}'s device so CardiTrack can start watching over them";

        // Baseline learning (M1-09e)
        LearningCard.IsVisible = data.Baseline.IsLearning && data.Device.HasActiveConnection;
        LearningLabel.Text =
            $"Learning {firstName}'s routine — day {data.Baseline.DaysCaptured} of {data.Baseline.DaysRequired}";
        LearningProgress.Progress = data.Baseline.PercentComplete / 100.0;

        // Metrics
        if (data.Metrics is { } metrics)
        {
            MetricsGrid.IsVisible = true;
            StepsCard.ApplySteps(metrics.Steps);
            HeartRateCard.ApplyHeartRate(metrics.RestingHeartRate);
            SleepCard.ApplySleep(metrics.Sleep);
        }
        else
        {
            MetricsGrid.IsVisible = false;
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
    /// Dims Call and Send Message when there is no number to act on (issue #67).
    /// </summary>
    /// <remarks>
    /// The tiles keep their tap handlers: a dimmed tile on touch has no hover state to explain
    /// itself with, so the tap has to. <c>ToolTipProperties</c> covers long-press and desktop.
    /// The tooltip names the emergency contact when we have it, because the tile is labelled
    /// with the CardiMember's name but dials someone else's number.
    /// </remarks>
    private void ApplyPhoneAvailability(DashboardResponse data, string firstName)
    {
        var hasPhone = !string.IsNullOrWhiteSpace(data.EmergencyContactPhone);
        var tooltip = hasPhone
            ? string.IsNullOrWhiteSpace(data.EmergencyContactName)
                ? $"Calls {firstName}'s emergency contact."
                : $"Calls {data.EmergencyContactName}, {firstName}'s emergency contact."
            : NoPhoneMessage(firstName);

        CallAction.Opacity = hasPhone ? 1 : UnavailableActionOpacity;
        MessageAction.Opacity = hasPhone ? 1 : UnavailableActionOpacity;

        ToolTipProperties.SetText(CallAction, tooltip);
        ToolTipProperties.SetText(MessageAction, tooltip);
    }

    /// <summary>
    /// Points at the emergency contact number, which is the one the add and edit forms actually
    /// capture — so this is advice the reader can act on.
    /// </summary>
    private static string NoPhoneMessage(string firstName) =>
        string.IsNullOrWhiteSpace(firstName)
            ? "Add an emergency contact number to this CardiMember to call from here."
            : $"Add an emergency contact number for {firstName} to call from here.";

    private void SetState(DashboardState state)
    {
        SkeletonPanel.IsVisible = state == DashboardState.Loading;
        ContentPanel.IsVisible = state == DashboardState.Loaded;
        NoMemberPanel.IsVisible = state == DashboardState.NoMember;
        ErrorPanel.IsVisible = state == DashboardState.Error;
    }

    private async void OnCallTapped(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastData?.EmergencyContactPhone))
        {
            await _popups.ShowInfoAsync(
                NoPhoneMessage(NameFormatting.FirstName(_lastData?.Name)), "No number yet");
            return;
        }
        try
        {
            PhoneDialer.Default.Open(_lastData.EmergencyContactPhone);
        }
        catch (Exception)
        {
            await _popups.ShowWarningAsync("Phone calls aren't supported on this device.");
        }
    }

    private async void OnMessageTapped(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastData?.EmergencyContactPhone))
        {
            await _popups.ShowInfoAsync(
                NoPhoneMessage(NameFormatting.FirstName(_lastData?.Name)), "No number yet");
            return;
        }
        try
        {
            await Sms.Default.ComposeAsync(new SmsMessage(string.Empty, _lastData.EmergencyContactPhone));
        }
        catch (Exception)
        {
            await _popups.ShowWarningAsync("Messaging isn't supported on this device.");
        }
    }

    private void OnViewDetailsTapped(object? sender, EventArgs e) => OpenMemberDetails();

    /// <summary>
    /// Both the hero card and the "View Details" action land on M1-13. The member id comes
    /// from the loaded dashboard rather than the cached preference, so it always matches
    /// whoever is actually on screen.
    /// </summary>
    private void OpenMemberDetails()
    {
        if (_lastData is not { } data)
            return;
        _ = Shell.Current.GoToAsync($"{CardiMemberDetailPage.Route}?memberId={data.CardiMemberId}");
    }

    private async void OnBellClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync(AppShell.AlertsRoute);

    private async void OnAlertTapped(object? sender, Guid alertId) =>
        await _popups.ShowInfoAsync("Alert details (M1-11) are on the way.", "Coming soon");

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

    private async void OnViewTrendsClicked(object? sender, EventArgs e) =>
        await _popups.ShowInfoAsync("Trends & history (M2-03) are on the way.", "Coming soon");
}
