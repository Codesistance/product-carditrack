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

    /// <summary>
    /// Every metric the trends carousel can show, in the order it swipes: icon, the colour key of
    /// that icon's own stroke, name, the format its headline reading takes, and the barer format
    /// its chart's own min/max labels take.
    /// </summary>
    /// <remarks>
    /// The ink is paired with the icon here rather than derived inside the card, because this is
    /// the one place that already knows a metric is the steps one — matching them anywhere else
    /// would mean a second table of metric identities to keep in step with this one.
    /// </remarks>
    private static readonly (string Icon, string Ink, string Name, string Value, string Axis,
        Func<DashboardMetrics, DashboardMetric> Select)[] TrendCards =
    [
        ("icon_metric_steps.svg", "MetricStepsInk", "Activity", "{0:N0} steps", "{0:N0}", m => m.Steps),
        ("icon_metric_heart.svg", "MetricHeartInk", "Heart Rate", "{0:N0} bpm", "{0:N0}", m => m.RestingHeartRate),
        ("icon_metric_sleep.svg", "MetricSleepInk", "Sleep", "{0:0.#} hours", "{0:0.#}", m => m.Sleep),
        ("icon_metric_temperature.svg", "MetricTemperatureInk", "Skin Temp", "{0:0.#}°C", "{0:0.#}", m => m.Temperature),
        ("icon_metric_spo2.svg", "MetricSpO2Ink", "Blood Oxygen", "{0:0.#}%", "{0:0.#}", m => m.SpO2),
        ("icon_metric_breathing.svg", "MetricBreathingInk", "Breathing Rate", "{0:0.#} brpm", "{0:0.#}", m => m.BreathingRate),
    ];

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

    private bool _pauseDurationsOpen;
    private bool _pauseDurationsAnimating;

    /// <summary>Guards Switch.Toggled while we rebuild or roll back alert-rule rows.</summary>
    private bool _applyingAlertRules;

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
            Apply(_member);
            SetState(loaded: true);

            // Fire-and-forget, not awaited: Apply already rendered the placeholder summary
            // copy, and the digest read is a separate round trip that shouldn't hold up the
            // rest of the screen or the pull-to-refresh spinner.
            _ = LoadDigestAsync(_memberId);
            _ = LoadQuestionnairesAsync(_memberId);
            _ = LoadAlertPreferencesAsync(_memberId);
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

        DeviceCountLabel.Text = member.ConnectedDeviceCount switch
        {
            0 => "None yet",
            1 => "1 Device",
            var n => $"{n} Devices",
        };
        LastContactLabel.Text = member.LastSyncedAt is { } lastSynced
            ? RelativeTime.Format(lastSynced)
            : "Not synced yet";

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
        // Call when a number exists; otherwise primary caregivers get an edit affordance so
        // adding one is one tap from this card rather than hunting the header pencil (#280).
        PhoneCallButton.IsVisible = hasPhone;
        PhoneEditButton.IsVisible = !hasPhone && member.IsPrimaryCaregiver;
        PhoneEditTarget.InputTransparent = !member.IsPrimaryCaregiver;

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
                firstName));
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

    private async void OnManageDevicesClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync($"{DeviceManagementPage.Route}?memberId={_memberId}");

    private async void OnQuestionsTapped(object? sender, EventArgs e)
    {
        var name = Uri.EscapeDataString(NameFormatting.FirstName(_member?.Name) ?? string.Empty);
        await Shell.Current.GoToAsync(
            $"{QuestionnairesPage.Route}?memberId={_memberId}&name={name}");
    }

    private async void OnViewAlertsClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync(AppShell.AlertsRoute);

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

            foreach (var cluster in prefs.Clusters)
            {
                var rulesStack = new VerticalStackLayout { Spacing = 0 };
                var first = true;
                foreach (var rule in cluster.Rules)
                {
                    if (!first)
                        rulesStack.Add(new BoxView { Style = (Style)resources["DividerLine"] });
                    first = false;
                    rulesStack.Add(BuildAlertRuleRow(rule, canManage, resources));
                }

                AlertRulesHost.Add(new Border
                {
                    Style = (Style)resources["ElevatedCard"],
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
                if (!canManage)
                {
                    _applyingAlertRules = true;
                    toggle.IsToggled = !args.Value;
                    _applyingAlertRules = false;
                    await _popups.ShowInfoAsync(
                        $"Only {NameFormatting.FirstName(_member.Name)}'s primary caregiver can change alert rules.",
                        "Not your call to make");
                    return;
                }

                var previous = !args.Value;
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
