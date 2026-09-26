using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Export;
using CardiTrack.Mobile.Core.Journal;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile;

/// <summary>
/// The Summaries tab: the member's daybook entries, newest first, one per finished day — searchable
/// over the whole history and narrowable by urgency and by how far back to look. A card opens the
/// review's own page (<see cref="JournalEntryPage"/>), which carries the full account and the
/// charts to read it against.
/// </summary>
/// <remarks>
/// <para>
/// Took the Family tab's slot. That page was a placeholder for family invitations, which are R3
/// work — a permanent quarter of the bottom navigation spent on a card that said "coming soon",
/// while the daybook entries had no surface at all. When family sharing does land it belongs under
/// Settings or scoped to a member, not back in the bar.
/// </para>
/// <para>
/// Every filter is applied server-side, before the page cap — a caregiver searching "oxygen" is
/// asking about their history, not about whichever page happened to load. The search is debounced
/// the way the questionnaires archive's is, so a caregiver typing "breathing" costs one request,
/// not nine.
/// </para>
/// <para>
/// Refreshes on resume but does not poll. Every other live surface in the app carries
/// <c>RefreshEvery(PeriodicRefresh.LiveDataInterval)</c> because what it shows can change within
/// the minute; a daybook entry is written once, at 02:00 in the member's own local time, and cannot
/// change afterwards. Polling it would be a request every thirty seconds for a list that moves
/// once a day.
/// </para>
/// </remarks>
[QueryProperty(nameof(PreselectMemberId), "memberId")]
public partial class JournalPage : ContentPage
{
    /// <summary>
    /// How many reviews one load asks for. A month is a page a caregiver actually scrolls; the
    /// service clamps anything larger anyway, and search narrows the history server-side rather
    /// than needing a bigger page. Shared with the push-triggered warm so the list it writes
    /// is the one this page peeks.
    /// </summary>
    private const int HistoryLimit = OfflineReadDefaults.JournalHistoryLimit;

    /// <summary>Lines of the review shown on the card before it is opened.</summary>
    private const int PreviewLines = 3;

    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(350);

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly IJournalExportFlow _export;

    private bool _returningFromPopup;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private bool _hasAnyReviews;

    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;

    /// <summary>
    /// The list on screen, saved or live — null until there is one, and nulled again whenever the
    /// question changes (cadence, member, search, filter), so the next load peeks the new
    /// question's saved answer rather than leaving the old list under a new filter.
    /// </summary>
    private IReadOnlyList<DigestResponse>? _lastReviews;
    private CancellationTokenSource? _searchDebounceCts;

    /// <summary>
    /// Which book the list is showing. Days by default — it is the cadence every plan writes and
    /// the one a caregiver checks most.
    /// </summary>
    private JournalCadence _cadence = JournalCadence.Daybook;

    private IReadOnlyList<CardiMemberResponse> _members = [];
    private Guid? _pendingMemberId;
    private Guid _memberId;
    private string? _memberFirstName;
    private string? _search;

    /// <summary>How the list is narrowed — urgency and window, from the filter sheet.</summary>
    private JournalListFilter _filter = JournalListFilter.None;

    /// <summary>
    /// A member to land filtered to, passed by the dashboard's member card and the member detail
    /// page — "show me their daybook" should arrive already about them. Consumed on the next
    /// load; an id not on the account (a stale deep link) falls through to the normal
    /// primary-member rule rather than showing an empty page about nobody.
    /// </summary>
    public string PreselectMemberId
    {
        set => _pendingMemberId =
            Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id) && id != Guid.Empty
                ? id
                : null;
    }

    public JournalPage(ICardiTrackApiClient api, IPopupService popups, IJournalExportFlow export)
    {
        InitializeComponent();
        this.HoldUntilInsetsApplied();
        _api = api;
        _popups = popups;
        _export = export;
        _feedback = new RefreshFeedback(SavedBanner, Updating);
        RenderCadence();
        this.RefreshWhenAppResumes(RefreshUnattendedAsync);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_popups.IsShowing || _returningFromPopup)
        {
            _returningFromPopup = false;
            return;
        }

        _ = LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _returningFromPopup = _popups.IsShowing;
        if (!_popups.IsShowing && !ScreenRefresh.IsOnScreen(this))
            _export.Cancel();
    }

    private Task RefreshUnattendedAsync() =>
        DateTime.UtcNow - _lastLoadedUtc < ResumeRefresh.MinimumGap
            ? Task.CompletedTask
            : LoadAsync(silent: true);

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync(force: true);
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync(force: true);

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.DashboardRoute);

    // ── Filters ──────────────────────────────────────────────────────────────

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ClearSearchButton.IsVisible = !string.IsNullOrEmpty(e.NewTextValue);

        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        _ = DebouncedSearchAsync(e.NewTextValue, cts.Token);
    }

    private async Task DebouncedSearchAsync(string? text, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDebounce, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        _search = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        await ReloadForNewQuestionAsync();
    }

    private void OnClearSearchClicked(object? sender, EventArgs e) => SearchEntry.Text = string.Empty;

    private async void OnDaysSegmentTapped(object? sender, TappedEventArgs e) =>
        await SwitchCadenceAsync(JournalCadence.Daybook);

    private async void OnWeeksSegmentTapped(object? sender, TappedEventArgs e) =>
        await SwitchCadenceAsync(JournalCadence.Weekbook);

    private async void OnMonthsSegmentTapped(object? sender, TappedEventArgs e) =>
        await SwitchCadenceAsync(JournalCadence.Monthbook);

    /// <summary>
    /// Moves the list to another book.
    /// </summary>
    /// <remarks>
    /// The filters carry over deliberately — a caregiver who narrowed to "act now" and switched to
    /// weeks asked the same question at a different altitude, and silently dropping their filter
    /// would answer a question they did not ask. <c>_hasAnyReviews</c> does not carry over: it
    /// gates the filter row, and a member with a year of Daybooks and no Weekbooks yet would
    /// otherwise get a filter panel over an empty week list. A filter or search that did carry
    /// over keeps its controls regardless (see <see cref="ShowsFilterControls"/>), or an empty
    /// filtered week list would hide the only way to undo it.
    /// </remarks>
    private async Task SwitchCadenceAsync(JournalCadence cadence)
    {
        if (_cadence == cadence)
            return;

        _cadence = cadence;
        _hasAnyReviews = false;
        RenderCadence();
        await ReloadForNewQuestionAsync();
    }

    /// <summary>Paints the selected segment and re-words what the page says it is showing.</summary>
    private void RenderCadence()
    {
        PaintSegment(DaysSegment, DaysSegmentLabel, JournalCadence.Daybook, "day");
        PaintSegment(WeeksSegment, WeeksSegmentLabel, JournalCadence.Weekbook, "week");
        PaintSegment(MonthsSegment, MonthsSegmentLabel, JournalCadence.Monthbook, "month");

        PaintFilterChrome();

        SearchEntry.Placeholder = $"Search the {_cadence.EntryName()}s";

        SemanticProperties.SetDescription(ExportHit, "Export Range");
        SemanticProperties.SetHint(
            ExportHit,
            $"Asks which {PeriodNoun(_cadence)}s to save, then saves those {_cadence.EntryName()}s");
    }

    /// <summary>
    /// One segment's selected state, in colour and in what a screen reader is told. The hint
    /// switches between "Showing" and "Shows" so the selection is audible, not only visible —
    /// colour alone would leave the control's whole state invisible to a screen reader.
    /// </summary>
    private void PaintSegment(Border segment, Label label, JournalCadence cadence, string period)
    {
        var selected = _cadence == cadence;

        segment.BackgroundColor = selected ? Tinted("White") : Colors.Transparent;
        label.TextColor = selected ? Tinted("PrimaryDark") : Tinted("BodyText");

        SemanticProperties.SetHint(
            segment,
            selected
                ? $"Showing one entry for each finished {period}"
                : $"Shows one entry for each finished {period}");
    }

    private bool HasActiveFilter => _search is not null || _filter.IsNarrowed;

    /// <summary>
    /// Whether the search box, the strip and the filter button belong on screen: once the book has
    /// had an entry to filter, and always while something is narrowing it. The second half is
    /// what keeps an empty filtered list undoable — after a cadence switch resets
    /// <c>_hasAnyReviews</c>, the pills and the search box are the only controls that clear it.
    /// </summary>
    private bool ShowsFilterControls => _hasAnyReviews || HasActiveFilter;

    /// <summary>
    /// Opens the filter sheet — the Alerts list's, with the journal's questions — on whose journal
    /// this is and how it is narrowed, and applies what comes back. Changing member clears nothing
    /// else: a caregiver comparing two members' weeks wants the same filters over both.
    /// </summary>
    private async void OnFilterTapped(object? sender, TappedEventArgs e)
    {
        if (_memberId == Guid.Empty)
            return;

        await RefreshMembersAsync();

        var current = new JournalFilterChoice(CurrentMember(), _filter);
        var chosen = await _popups.ChooseJournalFilterAsync(
            current,
            [.. _members.Select(ToFilterMember)],
            _cadence,
            HistoryLimit,
            CountAsync);
        if (chosen is null || chosen == current)
            return;

        if (chosen.Member.Id != _memberId)
            SelectMember(chosen.Member.Id, _members.FirstOrDefault(m => m.Id == chosen.Member.Id)?.DisplayFirstName());

        ApplyFilter(chosen.Filter);
    }

    /// <summary>Shows the list under <paramref name="filter"/>, from its saved answer if it has one.</summary>
    private void ApplyFilter(JournalListFilter filter)
    {
        _filter = filter;
        PaintFilterChrome();
        _ = ReloadForNewQuestionAsync();
    }

    /// <summary>
    /// How many entries a choice would list, for the sheet's button — the same page the list would
    /// load, search included, since the journal endpoint has no total to ask for instead.
    /// </summary>
    private async Task<int?> CountAsync(JournalFilterChoice choice, CancellationToken ct)
    {
        try
        {
            var (urgency, from) = choice.Filter.ToQuery(DateOnly.FromDateTime(DateTime.Now));
            var entries = await _api.GetJournalEntriesAsync(
                choice.Member.Id, _cadence, HistoryLimit, _search, from, urgency, ct);
            return entries.Count;
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the account's members again before the sheet offers them, as the Alerts list does on
    /// every open: Shell keeps this page for the app's life, so a member added or removed since
    /// the first load has to be offered — or not — under "Whose". A failed read keeps the list
    /// already held, which the sheet tops up with the member on screen.
    /// </summary>
    private async Task RefreshMembersAsync()
    {
        try
        {
            _members = await _api.GetCardiMembersAsync();
            PaintFilterChrome();
        }
        catch (ApiException)
        {
        }
    }

    private FilterMember CurrentMember() => new(_memberId, _memberFirstName ?? "Unnamed");

    private static FilterMember ToFilterMember(CardiMemberResponse member) =>
        new(member.Id, member.DisplayFirstName() ?? "Unnamed");

    /// <summary>Points the page, and the chat launcher on it, at one member.</summary>
    /// <remarks>
    /// <c>_hasAnyReviews</c> starts over for the same reason it does on a cadence switch: it says
    /// whether <em>this</em> member's book has had an entry to filter, and another member's history
    /// would otherwise put a search box over someone whose first entry has not been written.
    /// </remarks>
    private void SelectMember(Guid id, string? firstName)
    {
        if (id != _memberId)
            _hasAnyReviews = false;
        _memberId = id;
        _memberFirstName = firstName;
        ChatBot.MemberId = _memberId;
        ChatBot.MemberFirstName = _memberFirstName;
        PaintFilterChrome();
    }

    /// <summary>
    /// The header button, its count, the subtitle, and the strip — everything that says whose
    /// journal this is and what it is narrowed to.
    /// </summary>
    /// <remarks>
    /// The member names the subtitle rather than wearing a pill: the list is always one member's,
    /// so there is no "everyone" for a ✕ to widen to. It is named only once the account has more
    /// than one member — with one, "whose" has only one answer and the line keeps saying what the
    /// book is. The button waits for something to filter or someone to switch to, for the same
    /// reason the search box waits (see <see cref="ShowsFilterControls"/>).
    /// </remarks>
    private void PaintFilterChrome()
    {
        var parts = _filter.Parts();
        var narrowed = parts.Count > 0;
        var named = _members.Count > 1 && !string.IsNullOrWhiteSpace(_memberFirstName);

        FilterButtonHost.IsVisible = _memberId != Guid.Empty && (ShowsFilterControls || _members.Count > 1);
        FilterButton.BackgroundColor = Tinted(narrowed ? "PrimaryDark" : "White");
        FilterIcon.Source = narrowed ? "icon_filter_white.svg" : "icon_filter.svg";
        FilterCountBadge.IsVisible = narrowed;
        FilterCountLabel.Text = parts.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        SemanticProperties.SetDescription(
            FilterButton,
            narrowed ? $"Filter the journal, {parts.Count} on" : "Filter the journal");

        var book = _cadence switch
        {
            JournalCadence.Weekbook => "Weekbooks of finished weeks",
            JournalCadence.Monthbook => "Monthbooks of finished months",
            _ => "Daybooks of finished days",
        };
        var said = parts.Select(p => p.Label).ToList();
        if (named)
            said.Insert(0, _memberFirstName!);
        HeaderSubtitle.Text = narrowed || named
            ? string.Join(" · ", narrowed ? said : [.. said, book])
            : book;

        FilterStripHost.Clear();
        foreach (var (part, label) in parts)
            FilterStripHost.Add(FilterStripPill.Create(label, () => ApplyFilter(_filter.Without(part))));
        if (parts.Count > 1)
            FilterStripHost.Add(FilterStripPill.ClearAll(() => ApplyFilter(JournalListFilter.None)));
        FilterStrip.IsVisible = narrowed;
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A different question — cadence, member, search or filter — drops the list on
    /// screen first, so the load that follows puts up the saved answer to the new question (or
    /// the loading panel) rather than leaving the old list under a filter it does not match. It
    /// supersedes whatever is running: the caregiver has just asked something else, and a tap
    /// that did nothing because a slow load happened to be in flight is a tap they will repeat.
    /// </summary>
    private Task ReloadForNewQuestionAsync()
    {
        _lastReviews = null;
        return LoadAsync(force: true);
    }


    /// <param name="force">
    /// Supersedes a load already in flight rather than skipping — for anything the caregiver
    /// asked for by hand. Unattended loads wait their turn.
    /// </param>
    private async Task LoadAsync(bool silent = false, bool force = false)
    {
        if (_gate.IsLoading && !force)
            return;
        var ticket = _gate.Begin();

        if (_lastReviews is null)
            SetState(loading: true);

        try
        {
            // A deep link names the member; it wins over both the remembered selection and the
            // primary-member rule below, because the affordance that sent the caregiver here
            // promised them this member's daybook.
            if (_pendingMemberId is { } pending)
            {
                _pendingMemberId = null;
                if (_members.Count == 0)
                {
                    _members = await MembersAsync(ticket);
                    if (!_gate.IsCurrent(ticket))
                        return;
                }

                if (_members.FirstOrDefault(m => m.Id == pending) is { } chosen)
                    SelectMember(chosen.Id, chosen.DisplayFirstName());
            }

            // The same rule the dashboard and the device-setup launcher use for "which member",
            // so the three cannot drift apart about who the app means when it has not been told —
            // and the full list is kept, because the filter sheet offers every member on the
            // account once there is more than one to choose between.
            if (_memberId == Guid.Empty)
            {
                _members = await MembersAsync(ticket);
                if (!_gate.IsCurrent(ticket))
                    return;
                var member = PrimaryCardiMember.From(_members);
                if (member is null)
                {
                    EmptyDetailLabel.Text =
                        "Add the person you care about, and their days will be summarised here.";
                    SetState(empty: true);
                    return;
                }

                // Through SelectMember like every other way a member is chosen, so the chat
                // launcher opens about the journal on screen rather than asking again.
                SelectMember(member.Id, member.DisplayFirstName());
            }

            var (urgency, from) = _filter.ToQuery(DateOnly.FromDateTime(DateTime.Now));

            // The question is captured before the awaits: a caregiver who taps Weeks while Days
            // is still in flight must not have the day list painted over their week list when the
            // slower call lands — the gate drops the superseded load.
            var (memberId, cadence, search) = (_memberId, _cadence, _search);

            // The saved list for exactly this question goes up first when nothing is on screen;
            // the live one replaces it under the overlay. Finished days do not change, so when
            // the live list is the saved one over again nothing is redrawn and nothing announced.
            var outcome = await SnapshotRefresh.RunAsync(
                _api, _gate, ticket,
                peek: _lastReviews is null
                    ? ct => _api.PeekJournalEntriesAsync(memberId, cadence, HistoryLimit, search, from, urgency, ct)
                    : null,
                fetch: ct => _api.GetJournalEntriesAsync(memberId, cadence, HistoryLimit, search, from, urgency, ct),
                render: reviews =>
                {
                    _lastReviews = reviews;
                    RenderReviews(reviews);
                },
                _feedback,
                sameAs: SamePayload.Same);

            switch (outcome.Result)
            {
                case RefreshResult.Superseded:
                    return;
                case RefreshResult.NothingAndFailed:
                    _lastReviews = null;
                    ErrorDetailLabel.Text = outcome.Error!.Message;
                    SetState(error: true);
                    return;
            }

            if (outcome.IsFresh)
                _lastLoadedUtc = DateTime.UtcNow;
            else if (!silent && outcome.Error is not null)
            {
                // There is already a list on screen. Replacing it with an error panel would take
                // away reviews that are still perfectly readable — they describe finished days and
                // do not go stale — so the failure is said over the top of them instead.
                await _popups.ShowWarningAsync(outcome.Error.Message, "Couldn't refresh");
            }
        }
        catch (OperationCanceledException) when (!_gate.IsCurrent(ticket))
        {
            // The member resolution above runs outside SnapshotRefresh on this load's token, so
            // a newer load cancelling this one surfaces here as a raw cancellation — the cache
            // read rethrows it rather than dressing it as a transport failure. It is this page's
            // own doing and there is nothing to report; the newer load paints.
        }
        catch (ApiException ex)
        {
            // A superseded request can also report its cancellation as a transport failure.
            if (!_gate.IsCurrent(ticket))
                return;

            // The member list above; the entries read reports through its outcome.
            if (_lastReviews is null)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(error: true);
            }
            else if (!silent)
            {
                await _popups.ShowWarningAsync(ex.Message, "Couldn't refresh");
            }
        }
        finally
        {
            // Only the load that still owns the screen clears the pull spinner. A superseded one
            // stopping it would take the spinner off a pull that is still running.
            if (_gate.IsCurrent(ticket))
                Refresher.IsRefreshing = false;
            _gate.Release(ticket);
        }
    }

    /// <summary>
    /// The account's members, from the device if it has them. Resolving whose journal this is
    /// must not itself be a round trip: it happens before the entries can even be peeked, so a
    /// network call here would put the whole screen back behind the network on a cold start —
    /// the wait this page exists to remove. The saved list is enough to name the primary member
    /// and to size the filter sheet's "Whose"; the entries fetched under it are the live read,
    /// and the next load's own member fetch corrects the list if it has changed.
    /// </summary>
    /// <remarks>
    /// A saved <em>empty</em> list deliberately does not count, unlike every other peek in the
    /// app. Empty means "this account watches nobody", which the caller answers by painting the
    /// "add the person you care about" panel and returning — so nothing else on that pass goes
    /// and checks. A stale empty snapshot would therefore strand a caregiver who does have a
    /// member on the one screen state that says they have none, until they left and came back.
    /// One round trip is the right price for not doing that.
    /// </remarks>
    private async Task<IReadOnlyList<CardiMemberResponse>> MembersAsync(LoadTicket ticket)
    {
        if (await _api.PeekCardiMembersAsync(ticket.Token) is { Count: > 0 } saved)
            return saved;

        return await _api.GetCardiMembersAsync(ticket.Token);
    }

    /// <summary>One list of reviews onto the page — the same for a saved list and a live one.</summary>
    private void RenderReviews(IReadOnlyList<DigestResponse> reviews)
    {
        _hasAnyReviews = _hasAnyReviews || reviews.Count > 0;

        // Landing here is reading the journal, however the caregiver arrived — the dashboard
        // card's CardiJournal glyph stops being coloured for anything up to the newest entry
        // on screen. The newest loaded, not "now": the mark is the entry's own instant (see
        // AttentionMarks), and a filtered list still counts only what it actually showed.
        if (reviews.Count > 0)
            AttentionMarks.MarkSeen(AttentionMarks.Journal, _memberId, reviews.Max(r => r.GeneratedAtUtc));

        // The filter row appears once the member has ever had a review to filter, and then
        // stays: hiding it on an empty *filtered* result would take away the one control
        // that undoes the emptiness.
        FilterPanel.IsVisible = ShowsFilterControls;
        PaintFilterChrome();

        // Shown as soon as there is a member to read about, empty history or not: a caregiver
        // waiting on their first entries is the one who most needs to see that weeks exist.
        CadenceRow.IsVisible = true;

        if (reviews.Count == 0)
        {
            if (HasActiveFilter)
            {
                EmptyTitleLabel.Text = "No entries match";
                EmptyDetailLabel.Text =
                    "Nothing in their history matches these filters — clear one and look again.";
            }
            else
            {
                var who = string.IsNullOrWhiteSpace(_memberFirstName)
                    ? "their"
                    : $"{_memberFirstName}'s";

                EmptyTitleLabel.Text = $"No {_cadence.EntryName()} entries yet";

                // Said at the cadence's own scale, and honestly about what it waits for. A
                // week needs most of itself measured before it can be accounted for, so a
                // caregiver who has Daybooks but no Weekbook is not looking at a fault.
                EmptyDetailLabel.Text = _cadence switch
                {
                    JournalCadence.Weekbook =>
                        $"The first is written when {who} week turns, and needs most of the week's days to have carried readings.",
                    JournalCadence.Monthbook =>
                        $"The first is written when {who} month turns, and needs about half the month's days to have carried readings.",
                    _ => $"The first entry is written after {who} first full day of readings.",
                };
            }
            SetState(empty: true);
            return;
        }

        Render(reviews);
        SetState(loaded: true);
    }

    private void Render(IReadOnlyList<DigestResponse> reviews)
    {
        ReviewsHost.Clear();

        foreach (var review in reviews)
            ReviewsHost.Add(BuildCard(review));
    }

    /// <summary>
    /// One day's card: when, what it was about, how soon it asks for attention, and the first
    /// lines of the review. Opening it is a navigation, not an expansion — the full account now
    /// carries the trend charts, which is more than a list row can hold and stay a list.
    /// </summary>
    private View BuildCard(DigestResponse review)
    {
        var body = new Label
        {
            Text = review.Text,
            Style = Styled("Body2Dark"),
            MaxLines = PreviewLines,
            LineBreakMode = LineBreakMode.TailTruncation,
        };

        // A small button, not a link line: "Read" is the card's one action said quietly, and
        // the whole card is tappable anyway — this is the visible affordance, not the only one.
        var read = new Border
        {
            StrokeThickness = 0.5,
            Stroke = Tinted("PrimaryDark"),
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(14, 5),
            HorizontalOptions = LayoutOptions.Start,
            StrokeShape = new RoundRectangle { CornerRadius = 13 },
            Content = new Label
            {
                Text = "Read",
                FontFamily = "QuicksandSemiBold",
                FontSize = 12,
                TextColor = Tinted("PrimaryDark"),
            },
        };
        SemanticProperties.SetDescription(read, "Read");
        SemanticProperties.SetHint(read, $"Opens this {PeriodNoun(_cadence)}'s full entry");

        // The alert tiles' construction, borrowed whole: a coloured rounded rect underneath and
        // the white card inset 4px from its left edge, so the urgency reads as a rail rounding
        // into the corner rather than as a pill spending space in the heading. No urgency, no
        // rail — the strip stays white rather than inventing a tier.
        var inner = new Border
        {
            BackgroundColor = Tinted("White"),
            StrokeThickness = 0,
            Padding = new Thickness(15, 14, 14, 14),
            StrokeShape = new RoundRectangle { CornerRadius = CardCornerRadius },
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    Heading(review),
                    body,
                    read,
                },
            },
        };

        var card = new Border
        {
            BackgroundColor = JournalPresentation.UrgencyRailColor(review.Urgency) ?? Tinted("White"),
            StrokeThickness = 0,
            Padding = new Thickness(4, 0, 0, 0),
            StrokeShape = new RoundRectangle { CornerRadius = CardCornerRadius },
            Shadow = new Shadow
            {
                Brush = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["CardShadowBrush"],
                Opacity = 0.15f,
                Radius = 14,
                Offset = new Point(0, 4),
            },
            Content = inner,
        };

        var open = new TapGestureRecognizer
        {
            Command = new Command(async () => await Shell.Current.GoToAsync(
                $"{JournalEntryPage.Route}?memberId={_memberId}&date={review.LocalDate:yyyy-MM-dd}"
                + $"&cadence={_cadence.WireValue()}"
                + $"&name={Uri.EscapeDataString(_memberFirstName ?? string.Empty)}")),
        };
        card.GestureRecognizers.Add(open);
        read.GestureRecognizers.Add(open);

        return card;
    }

    private async void OnExportTapped(object? sender, EventArgs e)
    {
        if (_memberId == Guid.Empty)
            return;

        var today = DateOnly.FromDateTime(DateTime.Now);

        // The range is asked for in the unit on screen — days on the Daybook, whole weeks on
        // the Weekbook, whole months on the Monthbook. It used to take whatever window the
        // filter chip happened to hold, which is a different question ("how far back to look")
        // answered for a different purpose.
        if (await _popups.ChooseJournalRangeAsync(_cadence, today) is not { } range)
            return;

        var (from, to) = range;
        var name = _members.FirstOrDefault(m => m.Id == _memberId)?.Name
            ?? _memberFirstName
            ?? "CardiJournal";

        await _export.RunAsync(
            _memberId,
            name,
            from,
            to,
            JournalExportRequests.Audience(_cadence),
            entryDate: null,
            Updating);
    }

    /// <summary>What one entry of this cadence covers, as a caregiver would say it.</summary>
    internal static string PeriodNoun(JournalCadence cadence) => cadence switch
    {
        JournalCadence.Weekbook => "week",
        JournalCadence.Monthbook => "month",
        _ => "day",
    };

    /// <summary>
    /// What a card is titled when the generation produced no headline — entries written before
    /// headlines existed have none, and a fixed label beats an empty row. At the cadence's own
    /// scale, or a month would announce itself as a day.
    /// </summary>
    internal static string FallbackHeadline(JournalCadence cadence) =>
        $"The {PeriodNoun(cadence)} in full";

    /// <summary>The date, the generated headline, and the urgency pill on one row.</summary>
    private Grid Heading(DigestResponse review)
    {
        var heading = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 10,
        };

        var titles = new VerticalStackLayout { Spacing = 2 };
        titles.Add(new Label
        {
            Text = JournalPresentation.PeriodLabel(_cadence, review.LocalDate),
            Style = Styled("Body1SemiBoldDark"),
        });
        titles.Add(new Label
        {
            // The review's own headline names what that day was about. Falling back to a fixed
            // label rather than leaving the row empty: entries written before headlines existed
            // have none, and the apps have always titled themselves in that case.
            Text = string.IsNullOrWhiteSpace(review.Headline) ? FallbackHeadline(_cadence) : review.Headline,
            // Dark, like the preview under it: the headline is what the day was about, which
            // is the message, not a caption on it.
            Style = Styled("Body2Dark"),
        });
        heading.Add(titles);

        // No pill: the card's left rail carries the urgency now, the way the alert tiles do.
        return heading;
    }

    /// <summary>
    /// A style from the merged application dictionary.
    /// </summary>
    /// <remarks>
    /// Not <c>this.Resources</c>: a page's own dictionary holds only what that page declares, and
    /// this one declares nothing — every style here lives in the dictionaries App.xaml merges, so
    /// the indexer on the page throws rather than walking up to them. The same reason
    /// <c>BottomNavBar</c> fully qualifies its lookups. Worth a helper because the failure is a
    /// <see cref="KeyNotFoundException"/> thrown while building a card, which surfaces as a list
    /// that never arrives rather than as an error a caregiver could act on.
    /// </remarks>
    private static Style Styled(string key) =>
        (Style)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    /// <summary>A colour from the same place, for the same reason.</summary>
    private static Color Tinted(string key) =>
        (Color)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    /// <summary>The standard card corner (Styles.xaml), so this code-built card matches the styled ones.</summary>
    private static CornerRadius CardCornerRadius =>
        (CornerRadius)Microsoft.Maui.Controls.Application.Current!.Resources["CardCornerRadius"];

    private void SetState(
        bool loading = false, bool loaded = false, bool empty = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ReviewsHost.IsVisible = loaded;
        EmptyPanel.IsVisible = empty;
        ErrorPanel.IsVisible = error;
    }
}
