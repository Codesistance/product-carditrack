using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Extensions;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Alerts;
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

    /// <summary>Every metric the carousel swipes through — see <see cref="TrendMetricCatalogue"/>.</summary>
    private static IReadOnlyList<TrendMetricCatalogue.Entry> TrendCards => TrendMetricCatalogue.All;

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private readonly List<MetricTrend> _trends = [];
    private readonly List<BoxView> _trendIndicators = [];

    private Guid _memberId;
    private bool _isLoading;
    private bool _isBusy;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private CardiMemberDetailResponse? _member;

    /// <summary>
    /// Whether a generated summary is currently on screen. Guards the placeholder — see
    /// <see cref="Apply"/>.
    /// </summary>
    private bool _digestRendered;

    /// <summary>Open/close timing of the pause-duration drop down, matching AccordionSection.</summary>
    private const uint PauseDropdownMs = 200;

    private const string PauseDropdownAnimation = "pauseDropdown";

    /// <summary>How long the Medical Information / Alert Rules chevrons take to turn over.</summary>
    private const uint ChevronTurnMs = 200;

    private bool _pauseDurationsOpen;
    private bool _pauseDurationsAnimating;

    /// <summary>Guards Switch.Toggled while we rebuild or roll back alert-rule rows.</summary>
    private bool _applyingAlertRules;

    /// <summary>Rule id currently waiting on a PATCH — blocks overlapping toggles.</summary>
    private string? _alertRuleToggleInFlight;

    /// <summary>
    /// Whether the last thing to take the screen from this page was one of our own popups — see
    /// <see cref="OnDisappearing"/>.
    /// </summary>
    private bool _returningFromPopup;

    public CardiMemberDetailPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        BuildPauseDurations();
        PendingQuestionCard.AnswerSubmitted += OnQuestionAnswered;
        PendingQuestionCard.DismissRequested += OnQuestionDismissed;
        this.RefreshWhenAppResumes(RefreshUnattendedAsync);

        // Same reason and the same cadence as the dashboard: this screen is one CardiMember's
        // current state, and a caregiver watching it should not have to pull it down to find out
        // that it moved.
        this.RefreshEvery(PeriodicRefresh.LiveDataInterval, RefreshUnattendedAsync);

        TrendsCarousel.HeightRequest = MetricTrendCard.CardHeight;
        TrendsCarousel.PositionChanged += OnTrendPositionChanged;
        TrendWindowPicker.WindowChanged += OnTrendWindowChanged;
    }

    public string MemberId
    {
        set
        {
            _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
                ? id
                : Guid.Empty;
            // Whatever summary is on screen belongs to whoever was on screen before. It must not
            // be the reason the next CardiMember's placeholder is skipped.
            _digestRendered = false;
            PendingQuestionCard.IsVisible = false;
            QuestionsRow.IsVisible = false;
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // A popup of ours closing raises this too — it is a modal page, so dismissing it hands
        // the screen back exactly as being navigated to does. That is not an arrival: the
        // caregiver never left, and refetching under them re-runs Apply, which hands the trends
        // carousel a new ItemsSource and snaps it (and the scroll under it) back — the screen
        // visibly jumping the moment an explanation is dismissed. Nothing can have changed
        // server-side while a modal held the screen anyway, and the periodic tick is still
        // running underneath.
        if (_popups.IsShowing || _returningFromPopup)
        {
            _returningFromPopup = false;
            return;
        }

        // Otherwise always refetch: coming back from the edit screen or device management, the
        // cached copy is exactly the thing that just changed.
        _ = LoadAsync();
    }

    /// <summary>
    /// Records that this page was covered rather than left, for the <c>OnAppearing</c> that
    /// follows. Both signals are kept because the platforms disagree on when the page underneath
    /// is raised relative to the modal leaving the stack: on the path where it is raised late,
    /// <see cref="IPopupService.IsShowing"/> has already been released and this is what remains.
    /// </summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _returningFromPopup = _popups.IsShowing;

        // Where the caregiver was reading on the way out, so returning can put them back. Taken
        // here and not in the reload because by then the reading is already wrong: popping back
        // re-attaches and re-measures this page, and whatever the content above has done in the
        // meantime has already moved the scroll. Measured on device — a caregiver who left from
        // the Management rows was, by the first line of the reload, three sections higher. On the
        // way out the layout is still the one they were looking at.
        _anchorOnLeaving = CaptureScrollAnchor();
    }

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    /// <summary>
    /// The quiet reload behind both unattended paths — the app returning to the foreground, and
    /// the timer ticking while the caregiver watches — for the same reason OnAppearing refetches:
    /// this screen shows one CardiMember's current state, and it should be current. Silent: an
    /// unrequested refresh that fails leaves what is on screen alone.
    /// </summary>
    private Task RefreshUnattendedAsync() =>
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

            // Taken when the caregiver left if they left, and only otherwise from where the page
            // sits now. By the time this runs the pop has already re-measured the page, so a
            // reading taken here is of a scroll position that has moved.
            var anchor = _anchorOnLeaving ?? CaptureScrollAnchor();
            _anchorOnLeaving = null;
            Apply(_member);
            SetState(loaded: true);
            _ = RestoreScrollAnchorAsync(anchor);

            // Fire-and-forget, not awaited: Apply already rendered the placeholder summary
            // copy, and the digest read is a separate round trip that shouldn't hold up the
            // rest of the screen or the pull-to-refresh spinner.
            // Each of these lands above or around where the caregiver is reading and changes the
            // height of it — the digest rewrites the summary, the questionnaires add or remove a
            // whole card — so the anchor is re-asserted as each one finishes rather than only
            // after Apply. Restoring is a no-op when nothing moved.
            _ = LoadThenRestoreAsync(LoadDigestAsync(_memberId), anchor);
            _ = LoadThenRestoreAsync(LoadQuestionnairesAsync(_memberId), anchor);
            _ = LoadThenRestoreAsync(LoadAlertPreferencesAsync(_memberId), anchor);
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
        catch (Exception ex)
        {
            // The same hole this branch closed on DashboardPage and the medical notes page: a
            // fault while putting the data on screen escapes into a fire-and-forget OnAppearing
            // or an async void pull handler, nothing observes it, and the page keeps its skeleton
            // for the rest of the session with nothing to tap. This one is the busiest Apply in
            // the app — six trend cards, a digest, banners and the rule list — so it is the most
            // worth admitting a failure on rather than the least.
            ScreenRefresh.LogFailure(ex, this, "while loading");
            if (_member is null)
            {
                ErrorDetailLabel.Text = "Something went wrong while showing this page.";
                SetState(error: true);
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// Runs one of the page's follow-up loads and puts the caregiver's place back afterwards.
    /// </summary>
    /// <remarks>
    /// The restore is in a finally, and the load's failure is swallowed here rather than left to
    /// fault a discarded task. Each of these already handles the API refusing; what is left is the
    /// unexpected, and losing the caregiver's place is not the right response to it — the reading
    /// position is worth restoring precisely when something went wrong above it.
    /// </remarks>
    private async Task LoadThenRestoreAsync(Task load, ScrollAnchor? anchor)
    {
        try
        {
            await load;
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, this, "loading a follow-up section");
        }
        finally
        {
            await RestoreScrollAnchorAsync(anchor);
        }
    }

    /// <summary>
    /// The section that was under the top of the viewport, and how far past its own top the
    /// viewport had gone.
    /// </summary>
    private readonly record struct ScrollAnchor(View Section, double PastTop);

    /// <summary>Where the caregiver was reading when they navigated away. See <see cref="OnDisappearing"/>.</summary>
    private ScrollAnchor? _anchorOnLeaving;

    /// <summary>
    /// Notes where the caregiver is reading, in terms of the content rather than a pixel offset.
    /// </summary>
    /// <remarks>
    /// A pixel offset is what Shell already preserves, and preserving it is the bug: this page
    /// refetches whenever it is returned to — coming back from Device Management or the edit form,
    /// what changed is exactly what was edited — and <see cref="Apply"/> is then free to re-measure
    /// the summary copy, the trend cards and the banners above wherever the caregiver had scrolled
    /// to. Keep the offset and everything under it slides; someone who left from the Management
    /// rows came back to the middle of the page, which is the "it jumped" complaint. Anchoring to a
    /// section instead means the thing they were looking at is still where they left it, however
    /// much the content above it grew or shrank.
    /// </remarks>
    private ScrollAnchor? CaptureScrollAnchor()
    {
        var scrolled = DetailScroller.ScrollY;
        if (scrolled <= 0)
            return null;

        foreach (var section in ContentPanel.Children.OfType<View>())
        {
            if (section is { IsVisible: true, Height: > 0 } && section.Y + section.Height > scrolled)
                return new ScrollAnchor(section, scrolled - section.Y);
        }

        return null;
    }

    /// <summary>
    /// Puts the anchored section back under the top of the viewport.
    /// </summary>
    /// <remarks>
    /// The yield is load-bearing: the section's new Y means nothing until the layout pass that
    /// followed <see cref="Apply"/> has run, and without it this scrolls to where the section used
    /// to be. Unanimated, because this is meant to look like nothing happened — a visible glide
    /// would announce the very movement it exists to hide.
    /// </remarks>
    private async Task RestoreScrollAnchorAsync(ScrollAnchor? anchor)
    {
        if (anchor is not { } held)
            return;

        await Task.Yield();

        var target = Math.Max(0, held.Section.Y + held.PastTop);

        // Already there: skip the call rather than issue a scroll that moves nothing. This is the
        // common case, since the anchor is re-asserted after each follow-up load and usually only
        // the first one has anything to do.
        //
        // It is not a test for whether the caregiver has taken over. A caregiver who starts
        // scrolling while a refresh is in flight will still be moved back when it lands. Telling
        // their scrolling apart from the page's own is the problem: the content above shifts under
        // a reload and the offset changes on its own — measured moving 1158 to 889 with nobody
        // touching the screen — so a "has it moved unexpectedly" heuristic reads those as the
        // caregiver and abandons the restore, which is the bug this exists to fix. Left as is
        // deliberately: the window is the second or two a refresh takes, and being put back where
        // you were is the behaviour that was asked for.
        if (Math.Abs(target - DetailScroller.ScrollY) < 2)
            return;

        try
        {
            await DetailScroller.ScrollToAsync(0, target, animated: false);
        }
        catch (Exception)
        {
            // The page went away mid-refresh. Nothing to restore it to.
        }
    }

    private void Apply(CardiMemberDetailResponse member)
    {
        OfflineBanner.ApplyFrom(_api);

        Avatar.Apply(member.Name, member.PhotoUrl);
        NameLabel.Text = member.Name;
        AgeRelationshipLabel.Text = $"{member.Age} years old • {member.Relationship.GetDisplayName()}";

        WeatherChip.IsVisible = member.Weather is not null;
        if (member.Weather is { } weather)
        {
            WeatherGlyphLabel.Text = WeatherGlyph.For(weather.Condition);
            WeatherTemperatureLabel.Text = weather.TemperatureCelsius is { } temperature
                ? $"{temperature:F0}°C"
                : string.Empty;
        }

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
        // Only on the paused branch: Apply also runs on the periodic refresh, and closing a drop
        // down the caregiver is reading mid-refresh would be the refresh taking the choice away.
        if (member.MonitoringPaused)
            ResetPauseDurations();

        // Same four-tier pipeline freshness as the dashboard (red / amber / blue / green). Hidden
        // while paused: collection is deliberately stopped, so a coloured dot would misreport a
        // pause as a connection gap. The paused banner above is the status in that case.
        ConnectionStatusRow.IsVisible = !member.MonitoringPaused;
        var freshnessColor = (Color)Microsoft.Maui.Controls.Application.Current!.Resources[
            FreshnessColorKey(member.DataFreshness)];
        ConnectionStatusDot.Fill = freshnessColor;
        LastContactLabel.Text = member.LastSyncedAt is { } lastSynced
            ? $"Updated {RelativeTime.Format(lastSynced)}"
            : "Not synced yet";
        SemanticProperties.SetDescription(
            LastContactLabel, $"{member.DataFreshnessMessage}. {LastContactLabel.Text}");

        // The digest loads on its own round trip (LoadDigestAsync) and lands after this method has
        // returned, so writing the placeholder every time meant every refresh — including the
        // silent periodic one — shrank this card back to two lines and then grew it again a moment
        // later. That is two layout passes for a summary that has usually not changed at all, and
        // it shoves Key Metric Trends and everything under it down the page and back twice while
        // the caregiver is reading them. The placeholder is for a screen that has nothing better
        // on it; once a summary is up it stays up until there is a new one, which is the same
        // stance the failed-refresh path above takes.
        if (!_digestRendered)
        {
            SummaryTitleLabel.Text = "Still getting to know them";
            SummaryGeneratedLabel.IsVisible = false;
            SummaryLabel.Text = $"We'll summarise how {NameFormatting.FirstName(member.Name)} is doing here as soon as there's enough data to say something useful.";
            // Suggestions come from the same generation as the summary, so they are absent for
            // exactly the members the placeholder is for.
            SuggestionsCard.IsVisible = false;
        }

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
        // Call and message when a number exists; otherwise primary caregivers get an edit
        // affordance so adding one is one tap from this card rather than hunting the header
        // pencil (#280).
        PhoneCallButton.IsVisible = hasPhone;
        PhoneMessageButton.IsVisible = hasPhone;
        PhoneEditButton.IsVisible = !hasPhone && member.IsPrimaryCaregiver;
        PhoneEditTarget.InputTransparent = !member.IsPrimaryCaregiver;

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
            if (memberId != _memberId)
                return;

            // The headline is generated with the summary and describes this particular one. A
            // digest stored before headlines existed has none, so the card falls back to naming
            // what it is rather than rendering a blank title.
            var headline = string.IsNullOrWhiteSpace(digest.Headline) ? "Latest Summary" : digest.Headline;
            var unchanged = _digestRendered
                            && SummaryTitleLabel.Text == headline
                            && SummaryLabel.Text == digest.Text;

            SummaryTitleLabel.Text = headline;
            SummaryLabel.Text = digest.Text;
            SummaryGeneratedLabel.Text = $"Updated {RelativeTime.Format(digest.GeneratedAtUtc)}";
            SummaryGeneratedLabel.IsVisible = true;
            _digestRendered = true;

            ApplySuggestion(digest.Suggestion);
            ApplyUrgency(digest.Urgency);

            if (unchanged)
                return;

            // Reads as an update rather than a flicker, and only when the words actually moved —
            // same treatment as the dashboard's status hero.
            SummaryTitleLabel.Opacity = 0;
            SummaryLabel.Opacity = 0;
            _ = SummaryTitleLabel.FadeToAsync(1, 150, Easing.CubicOut);
            _ = SummaryLabel.FadeToAsync(1, 150, Easing.CubicOut);
        }
        catch (ApiException)
        {
            // Placeholder copy stays — see the field's own comment in Apply().
        }
    }

    /// <summary>
    /// Loads the questions asked about this member: the one still waiting goes on the page, and the
    /// row through to the rest appears once there is anything behind it.
    /// </summary>
    /// <remarks>
    /// Best-effort in the same way as the summary — a question is an extra, and a failed call
    /// leaves the page looking exactly as it does for a member with nothing to answer.
    /// </remarks>
    private async Task LoadQuestionnairesAsync(Guid memberId)
    {
        // A silent refresh must not close an editor someone is typing in. Same courtesy the pause
        // drop down gets; the cost is one stale card until the next load.
        if (PendingQuestionCard.IsEditing)
            return;

        try
        {
            var result = await _api.GetQuestionnairesAsync(memberId);
            if (memberId != _memberId)
                return;

            QuestionsRow.IsVisible = result.HasAny;

            var pending = result.Pending;
            if (pending is null)
            {
                PendingQuestionCard.IsVisible = false;
                return;
            }

            var alreadyShowing = PendingQuestionCard.IsVisible
                                 && PendingQuestionCard.Questionnaire?.Id == pending.Id;

            PendingQuestionCard.Apply(pending, NameFormatting.FirstName(_member?.Name));
            PendingQuestionCard.IsVisible = true;

            if (alreadyShowing)
                return;

            // Reads as the question arriving rather than as a flicker — the summary's treatment.
            PendingQuestionCard.Opacity = 0;
            _ = PendingQuestionCard.FadeToAsync(1, 150, Easing.CubicOut);
        }
        catch (ApiException)
        {
            // No card, no row, no error state: the page is complete without either.
        }
    }

    private async void OnQuestionAnswered(object? sender, string answer)
    {
        if (PendingQuestionCard.Questionnaire is not { } questionnaire || _isBusy)
            return;

        _isBusy = true;
        PendingQuestionCard.SetBusy(true);
        try
        {
            await _api.AnswerQuestionnaireAsync(
                questionnaire.Id, new AnswerQuestionnaireRequest { AnswerText = answer });

            // Straight off the page, with no thank-you popup: the answer is stored and readable
            // under Questions & Answers, and a caregiver who was doing something else does not
            // need a dialog to dismiss on the way back to it.
            PendingQuestionCard.CloseEditor();
            PendingQuestionCard.IsVisible = false;
            QuestionsRow.IsVisible = true;
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            // The editor stays open with the text intact, so retrying does not mean retyping.
            await _popups.ShowWarningAsync(ex.Message, "Couldn't save your answer");

            // Reconciles the case where someone else answered it first: the reload finds nothing
            // pending and takes the card away.
            _ = LoadQuestionnairesAsync(_memberId);
        }
        finally
        {
            _isBusy = false;
            PendingQuestionCard.SetBusy(false);
        }
    }

    private async void OnQuestionDismissed(object? sender, EventArgs e)
    {
        if (PendingQuestionCard.Questionnaire is not { } questionnaire || _isBusy)
            return;

        // Confirmed because it is permanent, but as an offer rather than a caution — skipping a
        // question is a perfectly ordinary thing to do.
        var confirmed = await _popups.ConfirmInfoAsync(
            "We won't ask this one again.", "Skip this question?", "Yes, skip", "Keep it");
        if (!confirmed)
            return;

        _isBusy = true;
        PendingQuestionCard.SetBusy(true);
        try
        {
            await _api.DismissQuestionnaireAsync(questionnaire.Id);
            PendingQuestionCard.IsVisible = false;
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't skip that question");
        }
        finally
        {
            _isBusy = false;
            PendingQuestionCard.SetBusy(false);
        }
    }

    /// <summary>
    /// Shows the "Tips" message under the summary, or hides the section when this generation
    /// produced none.
    /// </summary>
    private void ApplySuggestion(string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion))
        {
            SuggestionsCard.IsVisible = false;
            return;
        }

        SuggestionsTitleLabel.Text = "Tips";
        SuggestionLabel.Text = suggestion;
        SuggestionsCard.IsVisible = true;
    }

    /// <summary>
    /// Shows the model's own urgency read beside the summary — alongside, never instead of, the
    /// card's dashboard-driven status colour. Hidden when this generation returned nothing
    /// parseable, the same treatment every optional digest field gets.
    /// </summary>
    private void ApplyUrgency(string? urgency)
    {
        var (colorKey, text) = urgency switch
        {
            "watch" => ("StatusGreen", "Nothing pressing today"),
            "check-in" => ("StatusYellow", "Worth a check-in today"),
            "concerning" => ("StatusOrange", "Worth prompt attention"),
            "act-now" => ("StatusRed", "Worth acting on right away"),
            _ => (null, null),
        };

        UrgencyRow.IsVisible = colorKey is not null;
        if (colorKey is null)
            return;

        var color = (Color)Microsoft.Maui.Controls.Application.Current!.Resources[colorKey];
        UrgencyDot.Fill = color;
        UrgencyLabel.TextColor = color;
        UrgencyLabel.Text = text;
    }

    /// <summary>
    /// Color token for each <see cref="CardiMemberDetailResponse.DataFreshness"/> tier. Same map
    /// as the dashboard: an unrecognised value falls back to unknown, not green.
    /// </summary>
    private static string FreshnessColorKey(string tier) => tier switch
    {
        "red" => "StatusRed",
        "amber" => "StatusYellow",
        "blue" => "StatusBlue",
        "green" => "StatusGreen",
        _ => "StatusUnknown",
    };

    /// <summary>
    /// Rebuilds the trends carousel, one card per metric this member actually reports. The
    /// caregiver's chosen window survives a refresh, and so does the card they were looking at —
    /// pulling to refresh should not shuffle the screen back to the first metric under them.
    /// </summary>
    private void ApplyTrends(DashboardMetrics? metrics)
    {
        var position = TrendsCarousel.Position;
        var firstName = NameFormatting.FirstName(_member?.Name);

        var reported = TrendCards
            .Where(card => metrics is not null && card.Select(metrics).Value is not null)
            .ToList();

        // The usual refresh brings new numbers for exactly the metrics already on screen, and
        // those go into the items the carousel is already holding: the realised cards redraw
        // themselves off the change (MetricTrendCard subscribes to it), and the carousel is left
        // alone. Handing it a new ItemsSource re-realises every card and re-measures the page
        // around it, which is a visible jolt on a screen someone is mid-read of — and the reason
        // a background tick used to move it under them. Rebuilding is for a genuine change of
        // shape: a device that has started reporting a metric it did not before, or a member
        // whose name the copy on the cards is written around.
        if (reported.Count > 0
            && reported.Count == _trends.Count
            && reported.Zip(_trends).All(pair => pair.First.Name == pair.Second.Name)
            && _trends[0].MemberFirstName == firstName)
        {
            foreach (var (card, trend) in reported.Zip(_trends))
                trend.Metric = card.Select(metrics!);
            return;
        }

        _trends.Clear();
        foreach (var (icon, ink, name, value, axis, select) in reported)
        {
            _trends.Add(new MetricTrend(
                icon, ink, name, value, axis, select(metrics!), TrendWindowPicker.SelectedDays,
                firstName)
            {
                MemberId = _memberId,
            });
        }

        // Assigning the same list instance back would not re-run the carousel's own diffing, so
        // hand it a fresh snapshot; the cards themselves are recycled either way.
        TrendsCarousel.ItemsSource = _trends.ToList();
        TrendsSection.IsVisible = _trends.Count > 0;
        if (_trends.Count == 0)
        {
            BuildTrendIndicators(0);
            return;
        }

        BuildTrendIndicators(_trends.Count);
        TrendsCarousel.Position = Math.Clamp(position, 0, _trends.Count - 1);
        // Read back rather than trusting the write: a carousel that has not been laid out yet keeps
        // the position it had, and the dots must say whatever the carousel actually settled on.
        PaintTrendIndicators(TrendsCarousel.Position);
    }

    private void OnTrendWindowChanged(object? sender, int days)
    {
        // The cards redraw themselves off this — see MetricTrend's own remarks on why the window
        // lives on the item rather than being pushed into each realised card.
        foreach (var trend in _trends)
            trend.Days = days;
    }

    private void OnTrendPositionChanged(object? sender, PositionChangedEventArgs e) =>
        PaintTrendIndicators(e.CurrentPosition);

    private void BuildTrendIndicators(int count)
    {
        TrendIndicatorPanel.Clear();
        _trendIndicators.Clear();
        // A single metric is not a carousel; dots under it would promise a swipe that goes nowhere.
        TrendIndicatorPanel.IsVisible = count > 1;
        if (count <= 1)
            return;

        for (var i = 0; i < count; i++)
        {
            var dot = new BoxView { WidthRequest = 8, HeightRequest = 8, CornerRadius = 4 };
            _trendIndicators.Add(dot);
            TrendIndicatorPanel.Add(dot);
        }
    }

    private void PaintTrendIndicators(int position)
    {
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        for (var i = 0; i < _trendIndicators.Count; i++)
        {
            // Active pill widens, same treatment as the welcome carousel's indicators.
            _trendIndicators[i].WidthRequest = i == position ? 24 : 8;
            _trendIndicators[i].Color = (Color)resources[i == position ? "ActiveIndicator" : "InactiveIndicator"];
        }
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
    }

    // Back through the app's own history where there is any — this page is reached from the
    // dashboard, the Notifications inbox and the alerts list, and the arrow should return to
    // whichever of them the caregiver actually came from. The dashboard is the floor for the
    // cases with nothing behind it, such as a notification tap opening the app here.
    private async void OnBackClicked(object? sender, EventArgs e) =>
        await this.GoBackAsync(AppShell.DashboardRoute);

    private async void OnMedicalTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync($"{MedicalInformationPage.Route}?memberId={_memberId}");

    /// <summary>
    /// The one thing on this page that still opens in place. Its content is switches rather than
    /// reading, so there is nothing to travel for, and it is short enough not to bury the two rows
    /// under it the way the medical notes could.
    /// </summary>
    private void OnToggleAlertRulesTapped(object? sender, TappedEventArgs e)
    {
        AlertRulesBody.IsVisible = !AlertRulesBody.IsVisible;
        TurnChevron(AlertRulesChevron, AlertRulesBody.IsVisible);
    }

    /// <summary>
    /// Turns a collapsible row's chevron to point at what it opened. Same glyph rotated rather
    /// than a swap for a second one, and the same duration and easing
    /// <see cref="Controls.AccordionSection"/> uses, so every collapsible in the app moves alike.
    /// </summary>
    private static void TurnChevron(View chevron, bool isExpanded) =>
        _ = chevron.RotateToAsync(
            isExpanded ? 180 : 0,
            ChevronTurnMs,
            isExpanded ? Easing.CubicOut : Easing.CubicIn);

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

    /// <summary>
    /// Opens the platform SMS composer on this CardiMember's own number. Same handoff and the same
    /// two failure modes as the dashboard's Message quick action — see
    /// <see cref="Controls.QuickActionRow"/>, and the <c>&lt;queries&gt;</c> note in the Android
    /// manifest for why the composer has to be declared before it can be reached at all.
    /// </summary>
    private async void OnMessagePhoneTapped(object? sender, TappedEventArgs e)
    {
        var phone = _member?.Phone;
        if (string.IsNullOrWhiteSpace(phone))
            return;

        try
        {
            await Sms.Default.ComposeAsync(new SmsMessage(string.Empty, phone));
        }
        catch (FeatureNotSupportedException)
        {
            await _popups.ShowWarningAsync("Messaging isn't supported on this device.");
        }
        catch (Exception)
        {
            await _popups.ShowWarningAsync(
                "We couldn't open your messaging app. Try texting them from it directly.");
        }
    }

    private async void OnEditPhoneTapped(object? sender, TappedEventArgs e)
    {
        if (_member is not { IsPrimaryCaregiver: true })
            return;

        await Shell.Current.GoToAsync(
            $"{EditCardiMemberPage.Route}?memberId={_memberId}&focus={Uri.EscapeDataString(EditCardiMemberPage.FocusPhone)}");
    }

    private async void OnEditClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync($"{EditCardiMemberPage.Route}?memberId={_memberId}");

    private async void OnWeatherTapped(object? sender, TappedEventArgs e)
    {
        if (_member?.Weather is { } weather)
            await _popups.ShowWeatherAsync(weather);
    }

    private async void OnManageDevicesTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync($"{DeviceManagementPage.Route}?memberId={_memberId}");

    private async void OnQuestionsTapped(object? sender, EventArgs e)
    {
        var name = Uri.EscapeDataString(NameFormatting.FirstName(_member?.Name) ?? string.Empty);
        await Shell.Current.GoToAsync(
            $"{QuestionnairesPage.Route}?memberId={_memberId}&name={name}");
    }

    private async void OnViewAlertsClicked(object? sender, EventArgs e) =>
        // Naming the member is what lets back come back to *this* page rather than to whichever
        // member the dashboard would resolve on its own.
        await Shell.Current.GoToTabAsync(AppShell.AlertsRoute, $"memberId={_memberId}");

    /// <summary>
    /// The row does one of two things depending on where monitoring stands: while it is live the
    /// row is the drop down's header and only opens or closes the durations, and the pause itself
    /// happens in <see cref="OnPauseDurationTapped"/>. While it is paused there is nothing to
    /// choose, so the row resumes directly.
    /// </summary>
    private async void OnPauseMonitoringTapped(object? sender, TappedEventArgs e)
    {
        if (_member is null || _isBusy)
            return;

        if (!_member.IsPrimaryCaregiver)
        {
            await _popups.ShowInfoAsync(
                $"Only {NameFormatting.FirstName(_member.Name)}'s primary caregiver can pause monitoring.", "Not your call to make");
            return;
        }

        if (!_member.MonitoringPaused)
        {
            TogglePauseDurations();
            return;
        }

        _isBusy = true;
        try
        {
            _member = null;
            await _api.ResumeMonitoringAsync(_memberId);
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

    /// <summary>
    /// Builds the drop down's rows once, from the same <see cref="PauseDurations"/> table the
    /// confirmation text reads, so a duration cannot be offered under one label and applied as
    /// another.
    /// </summary>
    private void BuildPauseDurations()
    {
        foreach (var (label, hours) in PauseDurations)
        {
            PauseDurationsHost.Add(new BoxView { Style = (Style)App.Current!.Resources["DividerLine"] });

            var row = new Grid
            {
                HeightRequest = 44,
                // Clears the header row's pause icon, so a duration hangs under the label that
                // offered it rather than under the icon.
                Padding = new Thickness(34, 0, 0, 0),
            };
            row.Add(new Label
            {
                Text = label,
                Style = (Style)App.Current!.Resources["Body2Medium"],
                TextColor = (Color)App.Current!.Resources["Primary"],
                VerticalTextAlignment = TextAlignment.Center,
            });
            // The label and its hours travel together into the handler: nothing downstream has to
            // match one back to the other.
            row.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => OnPauseDurationTapped(label, hours)),
            });
            PauseDurationsHost.Add(row);
        }
    }

    private async void OnPauseDurationTapped(string label, int hours)
    {
        if (_member is null || _isBusy)
            return;

        // Closes before the confirmation opens — the choice has been made, and leaving the list
        // hanging open behind the popup reads as though it hasn't.
        CollapsePauseDurations();

        _isBusy = true;
        try
        {
            var firstName = NameFormatting.FirstName(_member.Name);
            var confirmed = await _popups.ConfirmWarningAsync(
                $"We'll stop collecting {firstName}'s health data and won't raise alerts until then.",
                $"Pause for {label}?",
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

    private void TogglePauseDurations()
    {
        if (_pauseDurationsAnimating)
            return;

        if (_pauseDurationsOpen)
            CollapsePauseDurations();
        else
            ExpandPauseDurations();
    }

    private void ExpandPauseDurations()
    {
        _pauseDurationsAnimating = true;
        _pauseDurationsOpen = true;

        var width = PauseRowLayout.Width > 0 ? PauseRowLayout.Width : Width;
        var targetHeight = PauseDurationsHost.Measure(width, double.PositiveInfinity).Height;

        this.AbortAnimation(PauseDropdownAnimation);
        new Animation(v => PauseDurationsClip.HeightRequest = v, PauseDurationsClip.Height, targetHeight)
            .Commit(this, PauseDropdownAnimation, 16, PauseDropdownMs, Easing.CubicOut, (_, _) =>
            {
                _pauseDurationsAnimating = false;
                // This row sits near the bottom of a long page, so the list it just opened can
                // land below the fold. MakeVisible scrolls only when that actually happened.
                _ = DetailScroller.ScrollToAsync(PauseRowLayout, ScrollToPosition.MakeVisible, animated: true);
            });

        // The row's chevron points right when closed; a quarter turn points it at what opened.
        _ = PauseRowChevron.RotateToAsync(90, PauseDropdownMs, Easing.CubicOut);
    }

    private void CollapsePauseDurations()
    {
        _pauseDurationsAnimating = true;
        _pauseDurationsOpen = false;

        this.AbortAnimation(PauseDropdownAnimation);
        new Animation(v => PauseDurationsClip.HeightRequest = v, PauseDurationsClip.Height, 0)
            .Commit(this, PauseDropdownAnimation, 16, PauseDropdownMs, Easing.CubicIn,
                (_, _) => _pauseDurationsAnimating = false);

        _ = PauseRowChevron.RotateToAsync(0, PauseDropdownMs, Easing.CubicIn);
    }

    /// <summary>
    /// Shuts the drop down without animating, for the one case that isn't a tap: the row has
    /// become "Resume Monitoring", and a list of durations under it would offer a choice that no
    /// longer exists.
    /// </summary>
    private void ResetPauseDurations()
    {
        this.AbortAnimation(PauseDropdownAnimation);
        _pauseDurationsAnimating = false;
        _pauseDurationsOpen = false;
        PauseDurationsClip.HeightRequest = 0;
        PauseRowChevron.Rotation = 0;
    }

    private async void OnRemoveMemberTapped(object? sender, TappedEventArgs e)
    {
        if (_member is null || _isBusy)
            return;

        if (!_member.IsPrimaryCaregiver)
        {
            await _popups.ShowInfoAsync(
                $"Only {NameFormatting.FirstName(_member.Name)}'s primary caregiver can remove them.", "Not your call to make");
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
            // Not GoToTabAsync: this page is the one thing back must not return to. The member it
            // describes has just been removed, so the route that names them would resolve to
            // nothing — and offering to go back to a person the caregiver has deleted would be
            // wrong even if it worked.
            TabNavigation.Origin.Clear();
            await Shell.Current.GoToAsync(AppShell.DashboardRoute);
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, $"Couldn't remove {NameFormatting.FirstName(_member?.Name)}");
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

    private async Task LoadAlertPreferencesAsync(Guid memberId)
    {
        AlertRulesSkeleton.IsVisible = AlertRulesHost.Children.Count == 0;
        try
        {
            var prefs = await _api.GetAlertPreferencesAsync(memberId);
            if (_memberId != memberId)
                return;
            RenderAlertRules(prefs);
        }
        catch (ApiException)
        {
            // Leave whatever was already rendered; a failed background load must not blank the card.
            if (AlertRulesHost.Children.Count == 0 && _memberId == memberId)
            {
                AlertRulesHost.Clear();
                AlertRulesHost.Add(new Label
                {
                    Text = "Couldn't load alert rules. Pull to refresh.",
                    Style = (Style)App.Current!.Resources["Body2"],
                });
            }
        }
        finally
        {
            // The rules land after the page does, and the row they open into grows to them:
            // nothing is measured or clipped here, so a caregiver who opened the row while it
            // was still a skeleton simply watches it fill.
            if (_memberId == memberId)
                AlertRulesSkeleton.IsVisible = false;
        }
    }

    private void RenderAlertRules(AlertPreferencesResponse prefs)
    {
        _applyingAlertRules = true;
        try
        {
            AlertRulesHost.Clear();
            var canManage = _member?.IsPrimaryCaregiver == true;
            var resources = App.Current!.Resources;

            // Ordered by availability, at both levels: rules that can actually be turned on or off
            // come before the ones still marked "Soon", and a cluster with nothing available in it
            // yet sinks below the clusters that have something. What the catalogue offers today is
            // what a caregiver came here to change; the reserved ids are a roadmap, and reading
            // past two of them to reach a switch made the list feel mostly unbuilt.
            //
            // Within a cluster the tie is broken by title rather than by the catalogue's order.
            // That order was editorial — the sequence the rules were written in — which is a
            // reasonable default for a list somebody reads through once and a poor one for a list
            // of switches somebody returns to for a specific rule. Alphabetical is the order a
            // reader can predict without knowing the catalogue.
            var clusters = prefs.Clusters
                .OrderBy(c => c.Rules.Any(r => r.IsImplemented) ? 0 : 1);

            foreach (var cluster in clusters)
            {
                var rulesStack = new VerticalStackLayout { Spacing = 0 };
                var first = true;
                foreach (var rule in AlertRuleOrder.ForDisplay(cluster.Rules))
                {
                    if (!first)
                        rulesStack.Add(new BoxView { Style = (Style)resources["DividerLine"] });
                    first = false;
                    rulesStack.Add(BuildAlertRuleRow(rule, canManage, resources));
                }

                // Outlined, not elevated: these sit inside the Alert Rules card now rather than
                // on the page ground, and a shadow only reads as depth against the ground.
                AlertRulesHost.Add(new Border
                {
                    Style = (Style)resources["OutlinedCard"],
                    Padding = new Thickness(14, 10),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            new Label
                            {
                                Text = cluster.Title,
                                Style = (Style)resources["Body1SemiBoldDark"],
                            },
                            new Label
                            {
                                Text = cluster.Description,
                                Style = (Style)resources["Body2"],
                            },
                            rulesStack,
                        },
                    },
                });
            }
        }
        finally
        {
            _applyingAlertRules = false;
        }
    }

    private View BuildAlertRuleRow(AlertRuleSettingResponse rule, bool canManage, ResourceDictionary resources)
    {
        var title = new Label
        {
            Text = rule.Title,
            Style = (Style)resources["Body1SemiBoldDark"],
            LineBreakMode = LineBreakMode.WordWrap,
        };
        var subtitle = new Label
        {
            Text = rule.IsImplemented ? rule.Description : $"{rule.Description} — coming soon",
            Style = (Style)resources["Body2"],
            LineBreakMode = LineBreakMode.WordWrap,
        };

        var textStack = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children = { title, subtitle },
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Auto),
            },
            ColumnSpacing = 12,
            Padding = new Thickness(0, 8),
        };
        grid.Add(textStack, 0);

        if (rule.IsImplemented)
        {
            var toggle = new Switch
            {
                IsToggled = rule.Enabled,
                IsEnabled = canManage,
                OnColor = (Color)resources["Primary"],
                VerticalOptions = LayoutOptions.Center,
            };
            toggle.Toggled += async (_, args) =>
            {
                if (_applyingAlertRules || _member is null)
                    return;

                if (_alertRuleToggleInFlight is not null)
                {
                    // Another PATCH is in flight — put the switch back and wait.
                    _applyingAlertRules = true;
                    toggle.IsToggled = !args.Value;
                    _applyingAlertRules = false;
                    return;
                }

                var previous = !args.Value;
                _alertRuleToggleInFlight = rule.Id;
                toggle.IsEnabled = false;
                try
                {
                    await _api.SetAlertRuleEnabledAsync(_memberId, rule.Id, args.Value);
                }
                catch (ApiException ex) when (!ex.IsSessionExpired)
                {
                    _applyingAlertRules = true;
                    toggle.IsToggled = previous;
                    _applyingAlertRules = false;
                    await _popups.ShowErrorAsync(ex.Message, "Couldn't update alert rule");
                }
                catch (ApiException)
                {
                    // Session gone — the app is already on its way back to sign-in.
                }
                finally
                {
                    _alertRuleToggleInFlight = null;
                    toggle.IsEnabled = canManage;
                }
            };
            grid.Add(toggle, 1);
        }
        else
        {
            grid.Add(new Label
            {
                Text = "Soon",
                Style = (Style)resources["Body2"],
                VerticalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center,
            }, 1);
        }

        return grid;
    }
}
