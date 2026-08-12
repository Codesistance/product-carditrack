using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>M1-10 Alerts List — every alert across the CardiMembers this caregiver watches.</summary>
public partial class AlertsPage : ContentPage
{
    /// <summary>
    /// Gap above the empty card, matching Figma. Two values because the card sits at the same
    /// y in both frames while the chip row above it is only present in one.
    /// </summary>
    private const double EmptyTopWithoutChips = 182;
    private const double EmptyTopWithChips = 124;

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private enum AlertsState { Loading, Loaded, Error }

    private bool _isLoading;
    private bool _showArchived;
    private CancellationTokenSource? _loadCts;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private AlertListResponse? _lastData;

    public AlertsPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        Filters.FilterChanged += OnFilterChanged;
        ApplyArchiveButtonText();
        this.RefreshWhenAppResumes(RefreshUnattendedAsync);

        // This screen had no timer at all — it only refreshed on re-entry and on resume, which
        // left a caregiver watching the alert list as the one person in the app who would not
        // see an alert arrive. Same tick as the dashboard and member detail.
        this.RefreshEvery(PeriodicRefresh.LiveDataInterval, RefreshUnattendedAsync);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Opening the list is a pull. It used to skip the load for two minutes after the last
        // one — on the screen whose whole job is telling a caregiver what has been raised.
        _ = RefreshUnattendedAsync();
    }

    /// <summary>
    /// The quiet reload behind all three unattended paths — arriving on the screen, the app
    /// returning to the foreground, and the timer above. A caregiver opening this screen is asking
    /// what has been raised since they last looked, and an alert list is the worst thing to serve
    /// stale, so the only gate is <see cref="ResumeRefresh.MinimumGap"/>, which just stops a load
    /// that has already run being repeated. Silent, because they did not ask for this one — a
    /// refresh that fails leaves the alerts already on screen alone rather than opening a dialog
    /// over them.
    /// </summary>
    private Task RefreshUnattendedAsync() =>
        DateTime.UtcNow - _lastLoadedUtc < ResumeRefresh.MinimumGap
            ? Task.CompletedTask
            : LoadAsync(silent: true);

    /// <param name="force">
    /// Supersedes a request already in flight rather than skipping. Anything the user asked
    /// for by hand — Refresh Now, pull-to-refresh, a different chip — must not be swallowed
    /// because a slow load happens to be running; that is the state the loading card is on
    /// screen for, so its own button would otherwise do nothing.
    /// </param>
    /// <param name="silent">
    /// Suppresses the "Couldn't refresh" popup for loads the user did not ask for.
    /// </param>
    private async Task LoadAsync(bool force = false, bool silent = false)
    {
        if (_isLoading && !force)
            return;

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _isLoading = true;

        if (_lastData is null)
            SetState(AlertsState.Loading);

        try
        {
            var (severity, status, from) = CurrentQuery();
            var data = await _api.GetAlertsAsync(severity, status, from, ct: cts.Token);
            if (cts.IsCancellationRequested)
                return;

            _lastData = data;
            _lastLoadedUtc = DateTime.UtcNow;
            Render(data);
            SetState(AlertsState.Loaded);

            // After the alerts, and isolated from them: this screen's job is health events, and a
            // failure fetching housekeeping must never cost the caregiver the list they came for.
            await LoadNudgeSectionAsync(cts.Token);
        }
        catch (ApiException ex)
        {
            // A superseded request reports its cancellation as a transport failure. That is
            // this page's own doing, so it must not surface as "no connection".
            if (cts.IsCancellationRequested)
                return;

            if (_lastData is null)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(AlertsState.Error);
            }
            else if (!silent)
            {
                // Alerts already on screen: a failed refresh must not blank a list someone
                // may be acting on, so say so and leave it.
                await _popups.ShowWarningAsync(ex.Message, "Couldn't refresh");
            }
        }
        finally
        {
            // Only the newest request owns the page's loading state.
            if (ReferenceEquals(_loadCts, cts))
            {
                _isLoading = false;
                Refresher.IsRefreshing = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// The chip selection as wire filters. Archived overrides the chips entirely — it is a
    /// different list, not a narrower one.
    /// </summary>
    private (string? Severity, string? Status, DateTime? From) CurrentQuery()
    {
        if (_showArchived)
            return (null, "resolved", null);

        // Local midnight, not UTC: "Today" has to mean the caregiver's today.
        var todayStart = DateTime.Today;

        return Filters.Selected switch
        {
            AlertFilter.Unread => (null, "new", null),
            AlertFilter.Critical => ("red", null, null),
            AlertFilter.Today => (null, null, todayStart),
            AlertFilter.ThisWeek => (null, null, todayStart.AddDays(-6)),
            _ => (null, null, null),
        };
    }

    private void Render(AlertListResponse data)
    {
        GroupsStack.Clear();

        var hasAlerts = data.Alerts.Count > 0;
        GroupsStack.IsVisible = hasAlerts;
        EmptyPanel.IsVisible = !hasAlerts;

        // A dead end otherwise: with no alerts and no way back, an empty archive would trap
        // the caregiver on a blank screen.
        ArchiveButton.IsVisible = hasAlerts || _showArchived;

        // Nothing to filter when the unfiltered list is genuinely empty — Figma's M1-10b drops
        // the chip row entirely, and an archive listing isn't chip-filtered at all.
        var isUnfiltered = Filters.Selected == AlertFilter.All && !_showArchived;
        Filters.IsVisible = !_showArchived && !(isUnfiltered && !hasAlerts);

        if (!hasAlerts)
        {
            var (title, detail) = isUnfiltered
                ? ("Nothing to worry about",
                   "CardiTrack is keeping an eye on things — we'll let you know if anything comes up")
                : ("No alerts match this filter",
                   "Try selecting a different filter to see more alerts");

            EmptyTitleLabel.Text = title;
            EmptyDetailLabel.Text = detail;
            EmptyPanel.Margin = new Thickness(
                0, Filters.IsVisible ? EmptyTopWithChips : EmptyTopWithoutChips, 0, 0);
            return;
        }

        EmptyPanel.Margin = new Thickness(0);

        var sectionTitle = (Style)Microsoft.Maui.Controls.Application.Current!.Resources["Heading3"];

        foreach (var group in GroupByDate(data.Alerts))
        {
            var section = new VerticalStackLayout { Spacing = 13 };
            section.Add(new Label { Text = group.Key, Style = sectionTitle });

            foreach (var alert in group)
            {
                var card = new AlertListCard();
                card.Apply(alert);
                card.CallRequested += OnCallRequested;
                card.AcknowledgeRequested += OnAcknowledgeRequested;
                card.DeleteRequested += OnDeleteRequested;
                section.Add(card);
            }

            GroupsStack.Add(section);
        }
    }

    /// <summary>
    /// Date buckets in the order M1-10 specifies: Today, Yesterday, This Week, Older.
    /// Timestamps arrive in UTC and are bucketed in local time, so an alert raised at 23:30
    /// local doesn't land under "Yesterday".
    /// </summary>
    private static IEnumerable<IGrouping<string, AlertSummaryResponse>> GroupByDate(
        IEnumerable<AlertSummaryResponse> alerts)
    {
        var today = DateTime.Today;

        return alerts
            .GroupBy(a =>
            {
                var local = DateTime.SpecifyKind(a.TriggeredAt, DateTimeKind.Utc).ToLocalTime().Date;
                if (local == today) return "Today";
                if (local == today.AddDays(-1)) return "Yesterday";
                return local > today.AddDays(-7) ? "This Week" : "Older";
            })
            .OrderBy(g => g.Key switch
            {
                "Today" => 0,
                "Yesterday" => 1,
                "This Week" => 2,
                _ => 3,
            });
    }

    private void SetState(AlertsState state)
    {
        SkeletonPanel.IsVisible = state == AlertsState.Loading;
        ErrorPanel.IsVisible = state == AlertsState.Error;
        ContentPanel.IsVisible = state == AlertsState.Loaded;

        // The chip row belongs to the list, not to the error or the first load.
        if (state != AlertsState.Loaded)
            Filters.IsVisible = state == AlertsState.Loading && !_showArchived;
    }

    private void OnFilterChanged(object? sender, AlertFilter filter)
    {
        // A filter change is a different query, so the cached page no longer applies —
        // dropping it puts the skeleton back rather than leaving stale rows under new chips.
        _lastData = null;
        _ = LoadAsync(force: true);
    }

    private async void OnArchiveClicked(object? sender, EventArgs e)
    {
        _showArchived = !_showArchived;
        _lastData = null;
        ApplyArchiveButtonText();
        if (!_showArchived)
            Filters.SetSelectedSilently(AlertFilter.All);
        await LoadAsync(force: true);
    }

    private void ApplyArchiveButtonText() =>
        ArchiveButton.Text = _showArchived ? "Back to current alerts" : "View Archived Alerts";

    /// <summary>
    /// Alerts is a tab root, so in the ordinary case there is no stack to pop and the arrow goes
    /// where it looks like it goes — back to the dashboard. It still asks
    /// <see cref="BackNavigation"/> first, so that stays true by the rule every other back arrow
    /// follows rather than by this page hard-coding it.
    /// </summary>
    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.DashboardRoute);

    private void OnPullToRefresh(object? sender, EventArgs e) => _ = LoadAsync(force: true);

    /// <summary>The error panel's "Try again" and the loading card's "Refresh Now".</summary>
    private void OnRefreshClicked(object? sender, EventArgs e) => _ = LoadAsync(force: true);

    /// <summary>
    /// Same offer the dashboard's call tiles make: ask, then open the profile form so the number
    /// can be added, rather than reporting the gap and leaving the caregiver to find the form.
    /// The form arrives pre-filled from the saved profile, so in the ordinary case the number is
    /// all that is left to type — it still validates the fields the API requires (name, date of
    /// birth, relationship), which a profile saved before those rules tightened could trip.
    /// </summary>
    private async void OnCallRequested(object? sender, AlertSummaryResponse alert)
    {
        if (string.IsNullOrWhiteSpace(alert.EmergencyContactPhone))
        {
            var firstName = NameFormatting.FirstName(alert.CardiMemberName);
            var prompt = string.IsNullOrWhiteSpace(firstName)
                ? "Would you like to add an emergency contact number for this CardiMember, so you can call them from here?"
                : $"Would you like to add an emergency contact number for {firstName}, so you can call them from here?";

            var addNow = await _popups.ConfirmInfoAsync(prompt, "No number yet", "Add number", "Not now");
            if (addNow)
                await Shell.Current.GoToAsync($"{EditCardiMemberPage.Route}?memberId={alert.CardiMemberId}");
            return;
        }

        try
        {
            PhoneDialer.Default.Open(alert.EmergencyContactPhone);
        }
        catch (FeatureNotSupportedException)
        {
            await _popups.ShowWarningAsync("Phone calls aren't supported on this device.");
        }
    }

    private async void OnAcknowledgeRequested(object? sender, AlertSummaryResponse alert)
    {
        if (sender is not AlertListCard card)
            return;

        card.SetBusy(true);
        try
        {
            var result = await _api.AcknowledgeAlertAsync(alert.AlertId);
            alert.Status = result.Status;
            alert.AcknowledgedAt = result.AcknowledgedAt;
            alert.AcknowledgedByUserId = result.AcknowledgedByUserId;
            if (_lastData is not null)
                _lastData.UnreadCount = result.UnreadCount;

            // Re-applied rather than reloaded: the caregiver is looking at this row, and a full
            // reload would reshuffle the list under their thumb.
            card.Apply(alert);
        }
        catch (ApiException ex)
        {
            card.SetBusy(false);
            await _popups.ShowWarningAsync(ex.Message, "Couldn't mark it handled");
        }
    }

    /// <summary>
    /// Removes an alert entirely — the caregiver's own housekeeping, distinct from
    /// acknowledging it. Confirmed first since there's no undo, then removed from view directly
    /// rather than reloaded, same as <see cref="OnAcknowledgeRequested"/>.
    /// </summary>
    private async void OnDeleteRequested(object? sender, AlertSummaryResponse alert)
    {
        if (sender is not AlertListCard card)
            return;

        var confirmed = await _popups.ConfirmWarningAsync(
            "This removes the alert from your list — it can't be undone.",
            "Remove this alert?", "Remove", "Cancel");
        if (!confirmed)
            return;

        try
        {
            await _api.DeleteAlertAsync(alert.AlertId);
            (card.Parent as Layout)?.Remove(card);
            if (_lastData is not null)
            {
                _lastData.Alerts = _lastData.Alerts.Where(a => a.AlertId != alert.AlertId).ToList();
                _lastData.Total = Math.Max(0, _lastData.Total - 1);
            }
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't remove it");
        }
    }

    // ------------------------------------------------------------------ completeness section

    /// <summary>
    /// Fills the "Also needs your attention" section — data-completeness items, kept in their own
    /// block below the health alerts rather than mixed into them.
    /// </summary>
    private async Task LoadNudgeSectionAsync(CancellationToken ct)
    {
        try
        {
            var summary = await _api.GetNotificationSummaryAsync(ct);

            NudgeStack.Clear();

            // Safety items first — they mean monitoring is degraded, which is the closest this
            // section gets to being about the person.
            var items = summary.SafetyBanners
                .Concat(summary.DashboardCards)
                .ToList();

            foreach (var item in items)
            {
                var row = new NudgeMiniRow(item, asSafetyBanner: item.Category == NotificationCategory.Safety);
                row.Tapped += OnNudgeTapped;
                NudgeStack.Add(row);
            }

            NudgeSection.IsVisible = items.Count > 0;
            NudgeSeeAllLink.IsVisible = summary.OpenCount > items.Count;
        }
        catch (ApiException)
        {
            NudgeSection.IsVisible = false;
        }
    }

    private async void OnNudgeTapped(object? sender, NotificationResponse notification) =>
        await Shell.Current.GoToAsync(NotificationsPage.Route);

    private async void OnSeeAllNudgesTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(NotificationsPage.Route);
}
