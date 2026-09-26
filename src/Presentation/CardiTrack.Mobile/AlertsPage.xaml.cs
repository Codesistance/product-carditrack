using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Alerts;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Core.Offline;
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
/// That narrowing — like every part of the filter — stays until the caregiver clears it, rather
/// than lapsing on the next visit. It is deliberately not self-clearing: this screen is reached
/// from a tab, a bell and a card, and a filter that quietly dropped itself somewhere between them
/// would leave a caregiver unsure which set they were looking at. The strip under the header is on
/// screen for exactly as long as a filter is, and each of its pills ends its own part.
/// </para>
/// </remarks>
[QueryProperty(nameof(FilterMemberId), "memberId")]
[QueryProperty(nameof(FilterMemberName), "memberName")]
public partial class AlertsPage : ContentPage
{
    /// <summary>
    /// Gap above the empty card, matching Figma. Two values because the card sits at the same
    /// y in both frames while the row above it (the filter strip, where Figma drew chips) is only
    /// present in one.
    /// </summary>
    private const double EmptyTopWithoutChips = 182;
    private const double EmptyTopWithChips = 124;

    /// <summary>What the header says under the title while nothing is narrowing the list.</summary>
    private const string DefaultSubtitle = "Everything that asked for your attention";

    private const string ArchiveSubtitle = "Alerts whose episode is over";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private enum AlertsState { Loading, Loaded, Error }

    private bool _isLoading;
    private bool _showArchived;

    /// <summary>
    /// Which load is the current one. A slow response from a superseded request must not paint
    /// over the chip the caregiver just tapped — cancellation alone is not enough when the HTTP
    /// call has already completed and its continuation is queued behind the UI thread.
    /// </summary>
    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private AlertListResponse? _lastData;
    private readonly HashSet<Guid> _pendingDeletes = [];

    /// <summary>
    /// What the list is narrowed to. Open alerts only outside the archive, whatever else is set:
    /// every view here is of the <em>current</em> alerts, so a resolved one never shows both here
    /// and under "View Archived Alerts" (see <see cref="AlertListFilter.ToQuery"/>).
    /// </summary>
    private AlertListFilter _filter = AlertListFilter.None;

    /// <summary>Whom the sheet offers, read once on the first open and kept for the page's life.</summary>
    private IReadOnlyList<AlertFilterMember>? _members;

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
    /// What the strip says. Only ever a label: the id above is what the query is built from, so a
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
        _feedback = new RefreshFeedback(SavedBanner, Updating);
        ApplyArchiveButtonText();
        PaintFilterChrome();
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
    /// Narrows the list to the CardiMember a route named, keeping whatever else the filter has.
    /// </summary>
    /// <remarks>
    /// A narrowing always gets a pill, even when no name came with it — the pill is how it is
    /// undone, so hiding it for want of a label would leave a caregiver on a list quietly missing
    /// most of their alerts with nothing to tap. The name stays null here, so the copy that speaks
    /// it — the empty state below — names nobody rather than addressing the caregiver about "This
    /// CardiMember"; only the pill wears the stand-in (<see cref="AlertListFilter.Parts"/>).
    /// </remarks>
    private void ApplyMemberFilter(Guid memberId, string? memberName) =>
        ApplyFilter(_filter with
        {
            MemberId = memberId,
            MemberName = string.IsNullOrWhiteSpace(memberName) ? null : memberName,
        });

    /// <summary>
    /// Shows the list under <paramref name="filter"/>. The cached page is dropped first: it
    /// answers a different question, and leaving it under a filter that has just changed is the
    /// stale-rows bug the old chips had (#308). LoadAsync then shows the device's saved page for
    /// the new query if it has one, and the loading card only if it has not.
    /// </summary>
    private void ApplyFilter(AlertListFilter filter)
    {
        _filter = filter;
        PaintFilterChrome();
        _lastData = null;
        _ = LoadAsync(force: true);
    }

    /// <summary>
    /// The header button, its count, the subtitle, and the strip — everything that says what the
    /// list is narrowed to. The status part drops out in the archive, where it does not apply.
    /// </summary>
    private void PaintFilterChrome()
    {
        var parts = _filter.Parts(_showArchived);
        var narrowed = parts.Count > 0;

        FilterButton.BackgroundColor = Resource<Color>(narrowed ? "PrimaryDark" : "White");
        FilterIcon.Source = narrowed ? "icon_filter_white.svg" : "icon_filter.svg";
        FilterCountBadge.IsVisible = narrowed;
        FilterCountLabel.Text = parts.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        SemanticProperties.SetDescription(
            FilterButton,
            narrowed ? $"Filter alerts, {parts.Count} on" : "Filter alerts");

        var subtitle = string.Join(" · ", parts.Select(p => p.Label));
        SubtitleLabel.Text = (_showArchived, narrowed) switch
        {
            (true, true) => $"Archive · {subtitle}",
            (true, false) => ArchiveSubtitle,
            (false, true) => subtitle,
            _ => DefaultSubtitle,
        };

        FilterStripHost.Clear();
        foreach (var (part, label) in parts)
            FilterStripHost.Add(FilterPill(label, () => ApplyFilter(_filter.Without(part))));
        if (parts.Count > 1)
            FilterStripHost.Add(ClearAllLink());
        FilterStrip.IsVisible = narrowed;
    }

    /// <summary>One applied part: its words and a ✕, the whole pill a tap that removes it.</summary>
    private static View FilterPill(string label, Action remove)
    {
        var pill = new Border
        {
            Padding = new Thickness(12, 6, 10, 6),
            StrokeThickness = 0,
            BackgroundColor = Resource<Color>("SelectedOptionBackground"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 15 },
            Content = new HorizontalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new Label
                    {
                        Text = label,
                        FontFamily = "QuicksandSemiBold",
                        FontSize = 13,
                        TextColor = Resource<Color>("PrimaryDark"),
                        VerticalTextAlignment = TextAlignment.Center,
                        LineBreakMode = LineBreakMode.TailTruncation,
                        MaximumWidthRequest = 160,
                    },
                    new Label
                    {
                        Text = "✕",
                        FontFamily = "QuicksandSemiBold",
                        FontSize = 11,
                        TextColor = Resource<Color>("PrimaryDark"),
                        VerticalTextAlignment = TextAlignment.Center,
                    },
                },
            },
        };
        SemanticProperties.SetDescription(pill, $"{label} filter");
        SemanticProperties.SetHint(pill, "Double tap to remove");
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => remove();
        pill.GestureRecognizers.Add(tap);
        return pill;
    }

    private View ClearAllLink()
    {
        var link = new Label
        {
            Text = "Clear all",
            Style = Resource<Style>("SectionLink"),
            VerticalTextAlignment = TextAlignment.Center,
            Padding = new Thickness(4, 6),
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => ApplyFilter(AlertListFilter.None);
        link.GestureRecognizers.Add(tap);
        return link;
    }

    /// <summary>
    /// Opens the filter sheet on the current filter and applies what comes back. The members are
    /// read on the first open only; a failure leaves the sheet offering "Everyone" and whoever the
    /// list is already narrowed to, which is still every choice that can be made correctly.
    /// </summary>
    private async void OnFilterTapped(object? sender, TappedEventArgs e)
    {
        var members = await MembersAsync();
        var chosen = await _popups.ChooseAlertFilterAsync(_filter, members, _showArchived, CountAsync);
        if (chosen is not null && chosen != _filter)
            ApplyFilter(chosen);
    }

    private async Task<IReadOnlyList<AlertFilterMember>> MembersAsync()
    {
        if (_members is not null)
            return _members;

        try
        {
            var members = await _api.GetCardiMembersAsync();
            return _members = members
                .Select(m => new AlertFilterMember(
                    m.Id, string.IsNullOrWhiteSpace(m.FirstName) ? m.Name : m.FirstName))
                .ToList();
        }
        catch (ApiException)
        {
            return [];
        }
    }

    /// <summary>
    /// How many alerts <paramref name="filter"/> would list, for the sheet's button. One row asked
    /// for, since only the total is wanted — the API counts the whole match whatever the page size.
    /// </summary>
    private async Task<int?> CountAsync(AlertListFilter filter, CancellationToken ct)
    {
        try
        {
            var (severity, status, from) = filter.ToQuery(DateTime.Today, _showArchived);
            var page = await _api.GetAlertsAsync(severity, status, from, limit: 1, cardiMemberId: filter.MemberId, ct: ct);
            return page.Total;
        }
        catch (ApiException)
        {
            return null;
        }
    }

    private static T Resource<T>(string key) =>
        (T)Microsoft.Maui.Controls.Application.Current!.Resources[key];

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
    /// for by hand — Refresh Now, pull-to-refresh, a different filter — must not be swallowed
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

        // Begin supersedes whatever is in flight — a new filter must win over a slow load (#308) —
        // and the gate's own check after every await is what stops the loser painting. It used to
        // be a hand-rolled generation counter and a CTS this page disposed itself, which could
        // abort the new load before SetState (#307, #308); the gate owns that now.
        var ticket = _gate.Begin();
        _isLoading = true;

        // Capture the filter at request start so a later change cannot let this response paint
        // under a different one — the gate drops the whole load if it was superseded. Local
        // midnight, not UTC: "Today" has to mean the caregiver's today.
        var (severity, status, from) = _filter.ToQuery(DateTime.Today, _showArchived);
        var memberFilterId = _filter.MemberId;

        var loadNudges = false;
        try
        {
            // Nothing on the wall yet — a cold start, or a filter that has just
            // changed the question. The page the device last saved for this exact query goes up
            // first and the live one is fetched behind it; the loading card is only for a query
            // the device has never answered. The saved rows can only be the new query's, because
            // the cache is keyed by it, so this is not the stale-rows-under-a-new-filter bug (#308)
            // coming back. Loading first, before the peek: the previous query's rows (or its
            // error) must not sit under the newly chosen filter for even the frame the cache read
            // takes.
            if (_lastData is null)
                SetState(AlertsState.Loading);

            var outcome = await SnapshotRefresh.RunAsync(
                _api, _gate, ticket,
                peek: _lastData is null
                    ? ct => _api.PeekAlertsAsync(severity, status, from, cardiMemberId: memberFilterId, ct: ct)
                    : null,
                fetch: ct => _api.GetAlertsAsync(severity, status, from, cardiMemberId: memberFilterId, ct: ct),
                render: data =>
                {
                    _lastData = data;
                    Render(data);
                    SetState(AlertsState.Loaded);
                },
                _feedback);

            switch (outcome.Result)
            {
                case RefreshResult.Superseded:
                    return;
                case RefreshResult.NothingAndFailed:
                    _lastData = null;
                    ErrorDetailLabel.Text = outcome.Error!.Message;
                    SetState(AlertsState.Error);
                    return;
            }

            if (outcome.IsFresh)
            {
                _lastLoadedUtc = DateTime.UtcNow;

                // Housekeeping only when the alerts themselves came from the API. The nudge
                // section is a second call to the same server this load just reached; asking it
                // over saved data means a request that will fail the same way, and its failure
                // hides a section that may already be showing something worth reading.
                loadNudges = true;
            }
            else if (!silent && outcome.Error is not null)
            {
                // Alerts already on screen: a failed refresh must not blank a list someone
                // may be acting on, so say so and leave it.
                await _popups.ShowWarningAsync(outcome.Error.Message, "Couldn't refresh");
            }
        }
        finally
        {
            // Release the list's loading state before housekeeping. Nudges used to sit inside the
            // try, so a hung summary call left pull-to-refresh spinning and blocked the next filter
            // load's finally from looking like the owner of the spinner (#307 / #308). The gate
            // itself is released only after the nudges, so a newer load can still cancel them.
            if (_gate.IsCurrent(ticket))
            {
                _isLoading = false;
                Refresher.IsRefreshing = false;
            }

            if (!loadNudges)
                _gate.Release(ticket);
        }

        if (!loadNudges)
            return;

        // After the alerts, and isolated from them: this screen's job is health events, and a
        // failure fetching housekeeping must never cost the caregiver the list they came for.
        // Still uses this load's ticket so a newer filter cancels the summary in flight.
        try
        {
            if (_gate.IsCurrent(ticket))
                await LoadNudgeSectionAsync(ticket);
        }
        finally
        {
            _gate.Release(ticket);
        }
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

        if (!hasAlerts)
        {
            var parts = _filter.Parts(_showArchived);
            var onlyMember = parts.Count == 1 && parts[0].Part == AlertFilterPart.Member;
            var (title, detail) = (_showArchived, parts.Count > 0, onlyMember, _filter.MemberName) switch
            {
                (false, false, _, _) => ("Nothing to worry about",
                    "CardiTrack is keeping an eye on things — we'll let you know if anything comes up"),
                (true, false, _, _) => ("Nothing in the archive yet",
                    "Alerts move here once their episode is over"),
                // Naming them is the difference between "there is nothing" and "there is nothing
                // for this one person", and a caregiver who narrowed the list by tapping a card
                // may not remember they did.
                (_, true, true, { } name) => ($"Nothing for {name} here",
                    "Remove their name above to see everyone's alerts"),
                _ => ("No alerts match these filters",
                    "Remove a filter above, or change it, to see more alerts"),
            };

            EmptyTitleLabel.Text = title;
            EmptyDetailLabel.Text = detail;
            EmptyPanel.Margin = new Thickness(
                0, FilterStrip.IsVisible ? EmptyTopWithChips : EmptyTopWithoutChips, 0, 0);
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
    }

    private async void OnArchiveClicked(object? sender, EventArgs e)
    {
        _showArchived = !_showArchived;
        _lastData = null;
        ApplyArchiveButtonText();
        // The filter carries over both ways — Pop's alerts stay Pop's in the archive — and only
        // its status part drops out there, so the chrome is repainted for that.
        PaintFilterChrome();
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
            var prompt = addPrompt(alert.MemberFirstName());
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

            // Under "Needs response", an acknowledged row no longer matches — drop it the same
            // way a delete does, rather than re-applying in place and leaving a handled card
            // under a filter that promised only new ones (#308).
            if (_filter.Status == AlertStatusChoice.NeedsResponse && !_showArchived)
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
    /// Fills the housekeeping section under the alerts: safety banners, then the shared
    /// "Complete the picture" card — kept in their own block rather than mixed into the alerts.
    /// </summary>
    private async Task LoadNudgeSectionAsync(LoadTicket ticket)
    {
        try
        {
            var summary = await _api.GetNotificationSummaryAsync(ticket.Token);
            if (!_gate.IsCurrent(ticket))
                return;

            SafetyBannerList.Clear();
            foreach (var banner in summary.SafetyBanners)
            {
                var row = new NudgeMiniRow(banner, asSafetyBanner: true);
                row.Tapped += OnNudgeTapped;
                SafetyBannerList.Add(row);
            }
            SafetyBannerList.IsVisible = summary.SafetyBanners.Count > 0;

            await CompleteThePicture.LoadAsync(_api, summary, ticket.Token);
            if (!_gate.IsCurrent(ticket))
                return;

            NudgeSection.IsVisible = SafetyBannerList.IsVisible || CompleteThePicture.IsVisible;
        }
        catch (OperationCanceledException) when (!_gate.IsCurrent(ticket))
        {
            // Superseded mid-read — leave whatever the newer load paints.
        }
        catch (ApiException) when (!_gate.IsCurrent(ticket))
        {
            // Superseded — leave whatever the newer load paints; do not blank the section.
        }
        catch (ApiException)
        {
            if (!_gate.IsCurrent(ticket))
                return;

            NudgeSection.IsVisible = false;
        }
    }

    private async void OnNudgeTapped(object? sender, NotificationResponse notification) =>
        await Shell.Current.GoToAsync(NotificationsPage.Route);
}
