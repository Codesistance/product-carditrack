using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>M1-10 Alerts List — every alert across the CardiMembers this caregiver watches.</summary>
/// <remarks>
/// <para>
/// Opened with a <c>memberId</c> it narrows to that one CardiMember — the journey off the
/// dashboard card's Alerts button, so a caregiver who tapped a particular relative's card is not
/// handed the whole household's alerts to sift back through.
/// </para>
/// <para>
/// That narrowing stays until the caregiver clears it on the chip, rather than lapsing on the next
/// visit. It is deliberately not self-clearing: this screen is reached from a tab, a bell and a
/// card, and a filter that quietly dropped itself somewhere between them would leave a caregiver
/// unsure which set they were looking at. The chip is on screen for exactly as long as the filter
/// is, and one tap ends both.
/// </para>
/// </remarks>
[QueryProperty(nameof(FilterMemberId), "memberId")]
[QueryProperty(nameof(FilterMemberName), "memberName")]
public partial class AlertsPage : ContentPage
{
    /// <summary>
    /// Gap above the empty card, matching Figma. Two values because the card sits at the same
    /// y in both frames while the chip row above it is only present in one.
    /// </summary>
    private const double EmptyTopWithoutChips = 182;
    private const double EmptyTopWithChips = 124;

    /// <summary>
    /// What the member chip says when the route narrowed the list but carried no name — a member
    /// whose profile has none, or a deep link that omitted it.
    /// </summary>
    private const string UnnamedMemberChipLabel = "This CardiMember";

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

    /// <summary>Which CardiMember the list is narrowed to, and the name the chip wears.</summary>
    private Guid? _memberFilterId;
    private string? _memberFilterName;

    /// <summary>
    /// Set by the query properties during navigation and spent on the next <c>OnAppearing</c>.
    /// Two steps rather than one because Shell hands these over before the page appears, and the
    /// chip has to be painted alongside the load that reads it, not a frame apart from it. Held as
    /// two fields rather than a pair, because Shell sets the two properties in whatever order the
    /// query string happens to be in and neither may depend on the other having arrived.
    /// </summary>
    private Guid? _pendingMemberFilterId;
    private string? _pendingMemberFilterName;

    /// <summary>
    /// The CardiMember to narrow to, from <c>//alerts?memberId=…</c>. An unparseable or empty id
    /// is no filter at all rather than a filter matching nobody — a stale deep link should show
    /// the whole list, not an empty one.
    /// </summary>
    public string FilterMemberId
    {
        set
        {
            _pendingMemberFilterId =
                Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id) && id != Guid.Empty
                    ? id
                    : null;

            // A name with no id to belong to is not a filter, and leaving it behind would let it
            // caption whichever member a later navigation does name.
            if (_pendingMemberFilterId is null)
                _pendingMemberFilterName = null;
        }
    }

    /// <summary>
    /// What the chip says. Only ever a label: the id above is what the query is built from, so a
    /// missing or mangled name costs the chip its wording, never the filter its meaning.
    /// </summary>
    public string FilterMemberName
    {
        set => _pendingMemberFilterName = Uri.UnescapeDataString(value ?? string.Empty);
    }

    public AlertsPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        Filters.FilterChanged += OnFilterChanged;
        Filters.MemberFilterCleared += OnMemberFilterCleared;
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

        // A member filter arriving on the route is the caregiver asking for a different list, so
        // it is spent before the load below rather than after it — and it forces that load, which
        // the unattended gap would otherwise swallow if they had just been here.
        //
        // Both fields are spent whether or not an id came with them. Shell sets the two query
        // properties in whatever order the query string happens to be in, so a name arriving
        // after an unusable id outlives the id's own guard below; clearing here is what stops it
        // surviving this navigation and captioning whichever member the next one names.
        var pendingId = _pendingMemberFilterId;
        var pendingName = _pendingMemberFilterName;
        (_pendingMemberFilterId, _pendingMemberFilterName) = (null, null);

        if (pendingId is { } memberId)
        {
            ApplyMemberFilter(memberId, pendingName);
            return;
        }

        // Opening the list is a pull. It used to skip the load for two minutes after the last
        // one — on the screen whose whole job is telling a caregiver what has been raised.
        _ = RefreshUnattendedAsync();
    }

    /// <summary>
    /// Narrows the list to one CardiMember, or — with a null id — widens it back to all of them.
    /// Either way the cached page is dropped first: it answers a different question, and leaving
    /// it under a chip that has just changed is the same stale-rows bug the filter chips had (#308).
    /// </summary>
    /// <remarks>
    /// A narrowing always gets a chip, even when no name came with it — the chip is the only way
    /// to undo one, so hiding it for want of a label would leave a caregiver on a list quietly
    /// missing most of their alerts with nothing to tap. The stand-in is the one
    /// <see cref="Controls.StatusHeroCard"/> already uses for a member it cannot name.
    /// </remarks>
    private void ApplyMemberFilter(Guid? memberId, string? memberName)
    {
        _memberFilterId = memberId;

        // The name stays null when none came with the id, so the copy that speaks it — the empty
        // state below — falls back to wording that names nobody rather than addressing the
        // caregiver about "This CardiMember". Only the chip, which must exist either way, wears
        // the stand-in.
        _memberFilterName = memberId is null || string.IsNullOrWhiteSpace(memberName)
            ? null
            : memberName;
        Filters.SetMemberFilter(
            memberId is null ? null : _memberFilterName ?? UnnamedMemberChipLabel);

        // LoadAsync decides between the saved page for the new filter and the loading card.
        _lastData = null;
        _ = LoadAsync(force: true);
    }

    private void OnMemberFilterCleared(object? sender, EventArgs e) =>
        ApplyMemberFilter(null, null);

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

        // Capture the chip at request start so a later tap cannot let this response paint under
        // a different filter — the generation check drops the whole load if it was superseded.
        var requestedFilter = Filters.Selected;
        var showArchived = _showArchived;
        var (severity, status, from) = QueryFor(requestedFilter, showArchived);

        var loadNudges = false;
        try
        {
            // Nothing on the wall yet — a cold start, or a chip or member filter that has just
            // changed the question. Put up the page the device last saved for this exact query,
            // if it has one, and fetch the live one behind it. The list used to open onto the
            // loading card on every landing while the previous answer sat encrypted on the
            // device; the loading card is now only for a query the device has never answered.
            // The saved rows can only be the new query's, because the cache is keyed by it, so
            // this is not the stale-rows-under-a-new-chip bug (#308) coming back.
            if (_lastData is null)
            {
                var saved = await _api.PeekAlertsAsync(
                    severity, status, from, cardiMemberId: _memberFilterId, ct: cts.Token);
                if (IsStale(generation, cts))
                    return;

                if (saved is not null)
                {
                    _lastData = saved;
                    Render(saved);
                    SetState(AlertsState.Loaded);
                }
                else
                {
                    SetState(AlertsState.Loading);
                }
            }

            var call = _api.GetAlertsAsync(
                severity, status, from, cardiMemberId: _memberFilterId, ct: cts.Token);
            var data = await call;
            if (IsStale(generation, cts))
                return;

            _lastData = data;
            _lastLoadedUtc = DateTime.UtcNow;
            Render(data);
            OfflineBanner.ApplyFrom(_api, call);
            SetState(AlertsState.Loaded);
            loadNudges = true;
        }
        catch (OperationCanceledException) when (IsStale(generation, cts))
        {
            // Cancellation during the HTTP body read is not wrapped as ApiException — treat it
            // the same as a superseded transport cancel so fire-and-forget callers stay quiet.
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
        // the chip row entirely, and an archive listing isn't chip-filtered at all. A member
        // filter is the exception on both counts: it is the one filter that survives into the
        // archive, so the row stays for its chip alone (StandardChipsVisible) rather than leaving
        // the archive quietly narrowed to one person with nothing on screen saying so.
        var isUnfiltered = Filters.Selected == AlertFilter.All
            && !_showArchived
            && _memberFilterId is null;
        Filters.StandardChipsVisible = !_showArchived;
        Filters.IsVisible = (!_showArchived || _memberFilterId is not null)
            && !(isUnfiltered && !hasAlerts);

        if (!hasAlerts)
        {
            var (title, detail) = (isUnfiltered, _memberFilterName) switch
            {
                (true, _) => ("Nothing to worry about",
                    "CardiTrack is keeping an eye on things — we'll let you know if anything comes up"),
                // Naming them is the difference between "there is nothing" and "there is nothing
                // for this one person", and a caregiver who narrowed the list by tapping a card
                // may not remember they did.
                (false, { } name) => ($"Nothing for {name} here",
                    "Tap their name above to see everyone's alerts, or try a different filter"),
                _ => ("No alerts match this filter",
                    "Try selecting a different filter to see more alerts"),
            };

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
                card.SosRequested += OnSosRequested;
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

        // The chip row belongs to the list, not to the error or the first load — except for the
        // member chip, which says what the load about to land is even a list of. The five behind
        // it follow the archive the same way Render has them do, so a load into the archive does
        // not flash chips that will be gone the moment it lands.
        if (state != AlertsState.Loaded)
        {
            Filters.StandardChipsVisible = !_showArchived;
            Filters.IsVisible = state == AlertsState.Loading
                && (!_showArchived || _memberFilterId is not null);
        }
    }

    private void OnFilterChanged(object? sender, AlertFilter filter)
    {
        // A filter change is a different query, so the page on screen no longer applies —
        // it is dropped rather than left under the newly highlighted chip while the request is
        // in flight (#308). LoadAsync then shows the device's saved page for the new query if it
        // has one, and the skeleton only if it has not.
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
    // The card's two phone actions mean what the dashboard's do — Call reaches the member, SOS
    // reaches their emergency contact. Call used to dial the emergency contact here, so the same
    // phone glyph meant two different people depending on which screen a caregiver was on.
    private async void OnCallRequested(object? sender, AlertSummaryResponse alert) =>
        await DialAsync(
            alert.CardiMemberPhone,
            alert,
            EditCardiMemberPage.FocusPhone,
            firstName => string.IsNullOrWhiteSpace(firstName)
                ? "Would you like to add a phone number, so you can call them from here?"
                : $"Would you like to add a phone number for {firstName}, so you can call them from here?");

    private async void OnSosRequested(object? sender, AlertSummaryResponse alert) =>
        await DialAsync(
            alert.EmergencyContactPhone,
            alert,
            EditCardiMemberPage.FocusEmergencyPhone,
            firstName => string.IsNullOrWhiteSpace(firstName)
                ? "Would you like to add an emergency contact number, so you can call them from here?"
                : $"Would you like to add an emergency contact number for {firstName}, so you can call them from here?");

    /// <summary>
    /// Dials <paramref name="number"/>, or — when there is none on file — offers the edit
    /// screen with the right field focused, in the same words the dashboard's tiles use.
    /// </summary>
    private async Task DialAsync(
        string? number, AlertSummaryResponse alert, string focusField, Func<string, string> addPrompt)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            var prompt = addPrompt(NameFormatting.FirstName(alert.CardiMemberName));
            var addNow = await _popups.ConfirmInfoAsync(prompt, "No number yet", "Add number", "Not now");
            if (addNow)
                await Shell.Current.GoToAsync(
                    $"{EditCardiMemberPage.Route}?memberId={alert.CardiMemberId}&focus={Uri.EscapeDataString(focusField)}");
            return;
        }

        try
        {
            PhoneDialer.Default.Open(number);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested || generation != _loadGeneration)
        {
            // Superseded mid-read — leave whatever the newer load paints.
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
