using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Extensions;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// M1-13 CardiMember Detail. Entered from the dashboard hero card or its "View Details"
/// action, and re-entered after M1-14/M1-15 so edits show up immediately.
/// </summary>
[QueryProperty(nameof(MemberId), "memberId")]
public partial class CardiMemberDetailPage : ContentPage
{
    /// <summary>Shell route; see <see cref="AppShell"/>.</summary>
    public const string Route = "memberdetail";

    private static readonly (string Label, int Hours)[] PauseDurations =
    [
        ("24 hours", 24),
        ("48 hours", 48),
        ("3 days", 72),
        ("1 week", 168),
    ];

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private Guid _memberId;
    private bool _isLoading;
    private bool _isBusy;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private CardiMemberDetailResponse? _member;

    public CardiMemberDetailPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        this.RefreshWhenAppResumes(RefreshOnResumeAsync);
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Always refetch: coming back from the edit screen or device management, the cached
        // copy is exactly the thing that just changed.
        _ = LoadAsync();
    }

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    /// <summary>
    /// The app returning to the foreground refetches, for the same reason OnAppearing does:
    /// this screen shows one CardiMember's current state, and the caregiver came back to read it
    /// now. Silent — an unrequested refresh that fails leaves what is on screen alone.
    /// </summary>
    private Task RefreshOnResumeAsync() =>
        DateTime.UtcNow - _lastLoadedUtc < ResumeRefresh.MinimumGap
            ? Task.CompletedTask
            : LoadAsync(silent: true);

    /// <param name="silent">
    /// Suppresses the "Couldn't refresh" popup for loads the user did not ask for.
    /// </param>
    private async Task LoadAsync(bool silent = false)
    {
        if (_isLoading)
            return;
        _isLoading = true;

        if (_member is null)
            SetState(loading: true);

        try
        {
            _member = await _api.GetCardiMemberAsync(_memberId);
            _lastLoadedUtc = DateTime.UtcNow;
            Apply(_member);
            SetState(loaded: true);

            // Fire-and-forget, not awaited: Apply already rendered the placeholder summary
            // copy, and the digest read is a separate round trip that shouldn't hold up the
            // rest of the screen or the pull-to-refresh spinner.
            _ = LoadDigestAsync(_memberId);
        }
        catch (ApiException ex)
        {
            if (_member is null)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(error: true);
            }
            else if (!silent)
            {
                // Something is already on screen; a failed refresh shouldn't blank it.
                await _popups.ShowWarningAsync(ex.Message, "Couldn't refresh");
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void Apply(CardiMemberDetailResponse member)
    {
        InitialsLabel.Text = NameFormatting.Initials(member.Name);
        NameLabel.Text = member.Name;
        AgeRelationshipLabel.Text = $"{member.Age} years old • {member.Relationship.GetDisplayName()}";

        PausedBanner.IsVisible = member.MonitoringPaused;
        if (member.MonitoringPaused)
        {
            var until = member.MonitoringPausedUntil is { } utc
                ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("MMM d, h:mm tt")
                : "further notice";
            PausedBannerLabel.Text = string.IsNullOrWhiteSpace(member.MonitoringPauseReason)
                ? $"Monitoring is paused until {until}."
                : $"Monitoring is paused until {until} — {member.MonitoringPauseReason}";
        }
        PauseRowLabel.Text = member.MonitoringPaused ? "Resume Monitoring" : "Pause Monitoring";

        DeviceCountLabel.Text = member.ConnectedDeviceCount switch
        {
            0 => "None yet",
            1 => "1 Device",
            var n => $"{n} Devices",
        };
        LastContactLabel.Text = member.LastSyncedAt is { } lastSynced
            ? RelativeTime.Format(lastSynced)
            : "Not synced yet";

        SummaryAccent.Color = (Color)Microsoft.Maui.Controls.Application.Current!.Resources[
            member.HealthStatus switch
            {
                "green" => "StatusGreen",
                "yellow" => "StatusYellow",
                "orange" => "StatusOrange",
                "red" => "StatusRed",
                _ => "StatusUnknown",
            }];
        // The digest itself loads separately (LoadDigestAsync) — this is just the placeholder
        // shown until it resolves, and the fallback if there isn't one yet.
        SummaryLabel.Text = "Getting to know this CardiMember's routine — a daily summary will appear here once there's enough to say.";

        ApplyTrends(member.Metrics);

        var hasEmergencyContact = !string.IsNullOrWhiteSpace(member.EmergencyContactName)
            || !string.IsNullOrWhiteSpace(member.EmergencyContactPhone);
        EmergencyNameLabel.Text = hasEmergencyContact
            ? member.EmergencyContactName ?? "Not named"
            : "No emergency contact yet";
        EmergencyPhoneLabel.Text = hasEmergencyContact
            ? member.EmergencyContactPhone ?? "No number"
            : "Add one so help is one tap away";
        EmergencyCallButton.IsVisible = !string.IsNullOrWhiteSpace(member.EmergencyContactPhone);

        var hasPhone = !string.IsNullOrWhiteSpace(member.Phone);
        PhoneLabel.Text = hasPhone ? member.Phone : "No phone number yet";
        PhoneCallButton.IsVisible = hasPhone;

        MedicalNotesLabel.Text = string.IsNullOrWhiteSpace(member.MedicalNotes)
            ? "No medical notes yet."
            : member.MedicalNotes;

        // Only a primary caregiver may edit, pause or remove — the API enforces this and
        // would answer 404, so showing the controls would just be a trap.
        EditButton.IsVisible = member.IsPrimaryCaregiver;
    }

    /// <summary>
    /// Best-effort, like the dashboard's live status line: no spinner, no error state. The
    /// placeholder <see cref="Apply"/> already rendered is a complete fallback on its own, so a
    /// 404 (nothing generated yet) or a failed call just leaves it as is.
    /// </summary>
    private async Task LoadDigestAsync(Guid memberId)
    {
        try
        {
            var digest = await _api.GetDigestAsync(memberId);
            if (memberId == _memberId)
                SummaryLabel.Text = digest.Text;
        }
        catch (ApiException)
        {
            // Placeholder copy stays — see the field's own comment in Apply().
        }
    }

    private void ApplyTrends(DashboardMetrics? metrics)
    {
        TrendsStack.Clear();
        if (metrics is null)
        {
            TrendsCard.IsVisible = false;
            return;
        }

        var rows = new (string Icon, string Name, DashboardMetric Metric, string Format)[]
        {
            ("icon_metric_steps.svg", "Activity", metrics.Steps, "{0:N0} steps"),
            ("icon_metric_heart.svg", "Heart Rate", metrics.RestingHeartRate, "{0:N0} bpm"),
            ("icon_metric_sleep.svg", "Sleep", metrics.Sleep, "{0:0.#} hours"),
            ("icon_metric_temperature.svg", "Skin Temp", metrics.Temperature, "{0:0.#}°C"),
            ("icon_metric_spo2.svg", "Blood Oxygen", metrics.SpO2, "{0:0.#}%"),
            ("icon_metric_breathing.svg", "Breathing Rate", metrics.BreathingRate, "{0:0.#} brpm"),
        };

        foreach (var (icon, name, metric, format) in rows)
        {
            if (metric.Value is null)
                continue;

            TrendsStack.Add(new MetricTrendRow(icon, name, string.Format(format, metric.Value.Value), metric));
        }

        TrendsCard.IsVisible = TrendsStack.Count > 0;
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
    }

    // Named rather than "..": this page is also reached via the Notifications inbox, where
    // there is no guarantee the dashboard sits directly underneath on the stack.
    private async void OnBackClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync(AppShell.DashboardRoute);

    private void OnToggleMedicalTapped(object? sender, TappedEventArgs e)
    {
        MedicalNotesLabel.IsVisible = !MedicalNotesLabel.IsVisible;
        MedicalChevron.Source = MedicalNotesLabel.IsVisible ? "icon_chevron.svg" : "icon_chevron_down.svg";
    }

    private async void OnCallEmergencyContactTapped(object? sender, TappedEventArgs e)
    {
        var phone = _member?.EmergencyContactPhone;
        if (string.IsNullOrWhiteSpace(phone))
            return;

        try
        {
            PhoneDialer.Default.Open(phone);
        }
        catch (Exception)
        {
            await _popups.ShowWarningAsync("Phone calls aren't supported on this device.");
        }
    }

    private async void OnCallPhoneTapped(object? sender, TappedEventArgs e)
    {
        var phone = _member?.Phone;
        if (string.IsNullOrWhiteSpace(phone))
            return;

        try
        {
            PhoneDialer.Default.Open(phone);
        }
        catch (Exception)
        {
            await _popups.ShowWarningAsync("Phone calls aren't supported on this device.");
        }
    }

    private async void OnEditClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync($"{EditCardiMemberPage.Route}?memberId={_memberId}");

    private async void OnManageDevicesClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync($"{DeviceManagementPage.Route}?memberId={_memberId}");

    private async void OnBackToDashboardTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(AppShell.DashboardRoute);

    private async void OnViewAlertsClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync(AppShell.AlertsRoute);

    private async void OnPauseMonitoringTapped(object? sender, TappedEventArgs e)
    {
        if (_member is null || _isBusy)
            return;

        if (!_member.IsPrimaryCaregiver)
        {
            await _popups.ShowInfoAsync(
                "Only this CardiMember's primary caregiver can pause monitoring.", "Not your call to make");
            return;
        }

        _isBusy = true;
        try
        {
            if (_member.MonitoringPaused)
            {
                _member = null;
                await _api.ResumeMonitoringAsync(_memberId);
                await LoadAsync();
                return;
            }

            var choice = await _popups.ChooseAsync(
                "Pause monitoring for how long?", "Cancel", PauseDurations.Select(d => d.Label).ToArray());
            if (choice is null)
                return;

            var hours = PauseDurations.First(d => d.Label == choice).Hours;
            var firstName = NameFormatting.FirstName(_member.Name);
            var confirmed = await _popups.ConfirmWarningAsync(
                $"We'll stop collecting {firstName}'s health data and won't raise alerts until then.",
                $"Pause for {choice}?",
                "Yes, pause");
            if (!confirmed)
                return;

            _member = null;
            await _api.PauseMonitoringAsync(_memberId, new PauseMonitoringRequest { DurationHours = hours });
            await LoadAsync();
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't change monitoring");
            await LoadAsync();
        }
        catch (ApiException)
        {
            // Session gone — the app is already on its way back to sign-in.
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async void OnRemoveMemberTapped(object? sender, TappedEventArgs e)
    {
        if (_member is null || _isBusy)
            return;

        if (!_member.IsPrimaryCaregiver)
        {
            await _popups.ShowInfoAsync(
                "Only this CardiMember's primary caregiver can remove them.", "Not your call to make");
            return;
        }

        var firstName = NameFormatting.FirstName(_member.Name);
        var confirmed = await _popups.ConfirmWarningAsync(
            $"Monitoring stops immediately and {firstName}'s devices are disconnected. " +
            "Their health history is kept for the retention period.",
            $"Remove {_member.Name}?",
            "Yes, remove");
        if (!confirmed)
            return;

        _isBusy = true;
        try
        {
            await _api.RemoveCardiMemberAsync(_memberId);
            // The dashboard resolves the primary member from scratch, so clearing the cached
            // id keeps it from asking for someone who no longer exists.
            Preferences.Default.Remove(DashboardPage.PrimaryMemberIdKey);
            await Shell.Current.GoToAsync(AppShell.DashboardRoute);
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't remove this CardiMember");
        }
        catch (ApiException)
        {
            // Session gone — the app is already on its way back to sign-in.
        }
        finally
        {
            _isBusy = false;
        }
    }
}
