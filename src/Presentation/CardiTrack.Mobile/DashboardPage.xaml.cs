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

    private readonly ICardiTrackApiClient _api;
    private readonly IAuthService _authService;
    private readonly IPopupService _popups;

    private enum DashboardState { Loading, Loaded, NoMember, Error }

    private bool _isLoading;
    private bool _wizardActive;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private DashboardResponse? _lastData;

    public DashboardPage(ICardiTrackApiClient api, IAuthService authService, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _authService = authService;
        _popups = popups;
        HeroCard.SyncRequested += (_, _) => _ = LoadAsync(force: true);
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
        await LoadAsync(force: true);
        Refresher.IsRefreshing = false;
    }

    private void OnRefreshClicked(object? sender, EventArgs e) => _ = LoadAsync(force: true);

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

        var firstName = data.Name.Split(' ')[0];
        CallLabel.Text = $"Call {firstName}";

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

    private void SetState(DashboardState state)
    {
        SkeletonPanel.IsVisible = state == DashboardState.Loading;
        ContentPanel.IsVisible = state == DashboardState.Loaded;
        NoMemberPanel.IsVisible = state == DashboardState.NoMember;
        ErrorPanel.IsVisible = state == DashboardState.Error;
    }

    private async void OnCallTapped(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastData?.Phone))
        {
            await _popups.ShowInfoAsync(
                "Add a phone number to this CardiMember to call them from here.", "No number yet");
            return;
        }
        try
        {
            PhoneDialer.Default.Open(_lastData.Phone);
        }
        catch (Exception)
        {
            await _popups.ShowWarningAsync("Phone calls aren't supported on this device.");
        }
    }

    private async void OnMessageTapped(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastData?.Phone))
        {
            await _popups.ShowInfoAsync(
                "Add a phone number to this CardiMember to message them from here.", "No number yet");
            return;
        }
        try
        {
            await Sms.Default.ComposeAsync(new SmsMessage(string.Empty, _lastData.Phone));
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
        await Shell.Current.GoToAsync("//alerts");

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
        catch (ApiException ex)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't start device setup");
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
