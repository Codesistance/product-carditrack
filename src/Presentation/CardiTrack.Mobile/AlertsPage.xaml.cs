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
    /// <summary>
    /// Bumps on every new load so a slow response from a cancelled request cannot paint over the
    /// chip the caregiver just tapped — CTS cancellation alone is not enough when the HTTP call
    /// has already completed and its continuation is queued behind the UI thread.
    /// </summary>
    private int _loadGeneration;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private AlertListResponse? _lastData;
    private readonly HashSet<Guid> _pendingDeletes = [];

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

        // Cancel can throw if a previous finally already disposed the source while another
        // caller still held the field — that used to abort the new load before SetState, so the
        // chip highlighted and the list never moved (#308) and the pull spinner never cleared (#307).
        CancelInFlightLoad();

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var generation = ++_loadGeneration;
        _isLoading = true;

        if (_lastData is null)
            SetState(AlertsState.Loading);

        // Capture the chip at request start so a later tap cannot let this response paint under
        // a different filter — the generation check drops the whole load if it was superseded.
        var requestedFilter = Filters.Selected;
        var showArchived = _showArchived;
        var (severity, status, from) = QueryFor(requestedFilter, showArchived);

        var loadNudges = false;
        try
        {
            var data = await _api.GetAlertsAsync(severity, status, from, ct: cts.Token);
            if (IsStale(generation, cts))
                return;

            _lastData = data;
            _lastLoadedUtc = DateTime.UtcNow;
            Render(data);
            SetState(AlertsState.Loaded);
            loadNudges = true;
        }
        catch (ApiException ex)
        {
            // A superseded request reports its cancellation as a transport failure. That is
            // this page's own doing, so it must not surface as "no connection".
            if (IsStale(generation, cts))
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
            // Release the list's loading state before housekeeping. Nudges used to sit inside the
            // try, so a hung summary call left pull-to-refresh spinning and blocked the next chip
            // load's finally from looking like the owner of the spinner (#307 / #308).
            //
            // Leave `_loadCts` pointing at this source until nudges finish so a newer load can
            // still Cancel() them — only clear the loading flags here.
            if (generation == _loadGeneration && ReferenceEquals(_loadCts, cts))
            {
                _isLoading = false;
                Refresher.IsRefreshing = false;
            }

            // `return` above still runs this finally, then exits the method — dispose here so a
            // superseded load cannot leak its CTS. A successful load disposes after nudges below.
            if (!loadNudges)
            {
                if (ReferenceEquals(_loadCts, cts))
                    _loadCts = null;
                cts.Dispose();
            }
        }

        if (!loadNudges)
            return;

        // After the alerts, and isolated from them: this screen's job is health events, and a
        // failure fetching housekeeping must never cost the caregiver the list they came for.
        // Still uses this load's token so a newer chip tap cancels the summary in flight.
        try
        {
            if (!IsStale(generation, cts))
                await LoadNudgeSectionAsync(generation, cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
                _loadCts = null;
            cts.Dispose();
        }
    }

    private void CancelInFlightLoad()
    {
        var inFlight = _loadCts;
        if (inFlight is null)
            return;

        try
        {
            inFlight.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished disposing — treat as cancelled.
        }
    }

    private bool IsStale(int generation, CancellationTokenSource cts) =>
        generation != _loadGeneration || cts.IsCancellationRequested;

    /// <summary>
    /// The chip selection as wire filters. Archived overrides the chips entirely — it is a
    /// different list, not a narrower one.
    /// </summary>
    private static (string? Severity, string? Status, DateTime? From) QueryFor(
        AlertFilter filter, bool showArchived)
    {
        if (showArchived)
            return (null, "resolved", null);

        // Local midnight, not UTC: "Today" has to mean the caregiver's today.
        var todayStart = DateTime.Today;

        return filter switch
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

        // A delete in flight must not reappear because the 30-second tick (or a resume) raced
        // the DELETE — the card left the screen when they asked, and only a confirmed failure
        // that still finds the row is allowed to put it back.
        var alerts = _pendingDeletes.Count == 0
            ? data.Alerts
            : data.Alerts.Where(a => !_pendingDeletes.Contains(a.AlertId)).ToList();

        var hasAlerts = alerts.Count > 0;
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

        foreach (var group in GroupByDate(alerts))
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
                card.OpenRequested += OnOpenRequested;
                section.Add(card);
            }

            GroupsStack.Add(section);
        }
    }

    /// <summary>
    /// Date buckets in the order M1-10 specifies: Today, Yesterday, This Week, Older.
    /// Daily-grain alerts are bucketed by the day they are about, not the instant they were
    /// raised, so yesterday's quieter day does not land under Today because the worker
    /// noticed it this afternoon. Timestamps still arrive in UTC and, when there is no
    /// <see cref="AlertSummaryResponse.AboutDate"/> yet, fall back to local raise time so an
    /// alert raised at 23:30 local doesn't land under "Yesterday".
    /// </summary>
    private static IEnumerable<IGrouping<string, AlertSummaryResponse>> GroupByDate(
        IEnumerable<AlertSummaryResponse> alerts)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        return alerts
            .GroupBy(a =>
            {
                var day = a.AboutDate != default
                    ? a.AboutDate
                    : DateOnly.FromDateTime(DateTime.SpecifyKind(a.TriggeredAt, DateTimeKind.Utc).ToLocalTime());
                if (day == today) return "Today";
                if (day == today.AddDays(-1)) return "Yesterday";
                return day > today.AddDays(-7) ? "This Week" : "Older";
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
        // dropping it and showing the skeleton immediately rather than leaving stale rows
        // under the newly highlighted chip while the request is in flight (#308).
        _lastData = null;
        SetState(AlertsState.Loading);
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
                ? "Would you like to add an emergency contact number, so you can call them from here?"
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

            // Under Unread, an acknowledged row no longer matches the chip — drop it the same
            // way a delete does, rather than re-applying in place and leaving a handled card
            // under a filter that promised only new ones (#308).
            if (Filters.Selected == AlertFilter.Unread && !_showArchived)
            {
                RemoveAlertFromCache(alert.AlertId);
                if (_lastData is not null)
                    Render(_lastData);
                return;
            }

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

    private async void OnOpenRequested(object? sender, AlertSummaryResponse alert) =>
        await Shell.Current.GoToAsync($"{AlertDetailPage.Route}?alertId={alert.AlertId}");

    /// <summary>
    /// Removes an alert entirely — the caregiver's own housekeeping, distinct from
    /// acknowledging it. Confirmed first since there's no undo. The card leaves the list
    /// immediately; it only comes back if the DELETE fails <em>and</em> the alert is still
    /// on the server. A 404 (already gone, or a timeout after a successful write) stays gone.
    /// </summary>
    private async void OnDeleteRequested(object? sender, AlertSummaryResponse alert)
    {
        if (sender is not AlertListCard)
            return;

        var confirmed = await _popups.ConfirmWarningAsync(
            "This removes the alert from your list — it can't be undone.",
            "Remove this alert?", "Remove", "Cancel");
        if (!confirmed)
            return;

        HideAlert(alert);

        try
        {
            await _api.DeleteAlertAsync(alert.AlertId);
        }
        catch (ApiException ex) when (ex.IsNotFound)
        {
            // The row is already gone — that's the outcome they asked for.
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            var stillThere = await AlertStillExistsAsync(alert.AlertId);
            if (stillThere)
            {
                RestoreAlert(alert);
                await _popups.ShowWarningAsync(ex.Message, "Couldn't remove it");
            }
        }
        finally
        {
            // Always drop the id once the attempt has finished — including a 401, which the
            // filters above do not catch. Leaving it in the set would hide a still-standing
            // alert from every later refresh if they sign back in on the same page instance.
            _pendingDeletes.Remove(alert.AlertId);
        }
    }

    /// <summary>
    /// Optimistic hide: drop the row from the cached page and rebuild, and remember the id so a
    /// refresh that still carries it cannot put it back while DELETE is in flight.
    /// </summary>
    private void HideAlert(AlertSummaryResponse alert)
    {
        _pendingDeletes.Add(alert.AlertId);
        RemoveAlertFromCache(alert.AlertId);
        if (_lastData is not null)
            Render(_lastData);
    }

    private void RemoveAlertFromCache(Guid alertId)
    {
        if (_lastData is null)
            return;

        var remaining = _lastData.Alerts.Where(a => a.AlertId != alertId).ToList();
        if (remaining.Count == _lastData.Alerts.Count)
            return;

        _lastData.Alerts = remaining;
        _lastData.Total = Math.Max(0, _lastData.Total - 1);
    }

    private void RestoreAlert(AlertSummaryResponse alert)
    {
        if (_lastData is null)
            return;

        if (_lastData.Alerts.All(a => a.AlertId != alert.AlertId))
        {
            _lastData.Alerts = _lastData.Alerts
                .Append(alert)
                .OrderByDescending(a => a.TriggeredAt)
                .ThenByDescending(a => a.AlertId)
                .ToList();
            _lastData.Total++;
        }

        Render(_lastData);
    }

    /// <summary>
    /// Whether the server still has this alert. A 404 is a definite no; any other failure
    /// cannot confirm existence, so the card stays hidden and the next successful list load
    /// will show it if it is still there.
    /// </summary>
    private async Task<bool> AlertStillExistsAsync(Guid alertId)
    {
        try
        {
            await _api.GetAlertAsync(alertId);
            return true;
        }
        catch (ApiException ex) when (ex.IsNotFound)
        {
            return false;
        }
        catch (ApiException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ completeness section

    /// <summary>
    /// Fills the "Also needs your attention" section — data-completeness items, kept in their own
    /// block below the health alerts rather than mixed into them.
    /// </summary>
    private async Task LoadNudgeSectionAsync(int generation, CancellationToken ct)
    {
        try
        {
            var summary = await _api.GetNotificationSummaryAsync(ct);
            if (generation != _loadGeneration)
                return;

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
        catch (ApiException) when (ct.IsCancellationRequested || generation != _loadGeneration)
        {
            // Superseded — leave whatever the newer load paints; do not blank the section.
        }
        catch (ApiException)
        {
            if (generation != _loadGeneration)
                return;

            NudgeSection.IsVisible = false;
        }
    }

    private async void OnNudgeTapped(object? sender, NotificationResponse notification) =>
        await Shell.Current.GoToAsync(NotificationsPage.Route);

    private async void OnSeeAllNudgesTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(NotificationsPage.Route);
}
