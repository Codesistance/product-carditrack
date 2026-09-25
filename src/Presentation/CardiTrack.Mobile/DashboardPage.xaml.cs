using System.Globalization;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Alerts;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Diagnostics;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Core.Navigation;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Core.Questionnaires;
using CardiTrack.Mobile.Onboarding;
using CardiTrack.Mobile.Services;
using Serilog;

namespace CardiTrack.Mobile;

public partial class DashboardPage : ContentPage
{
    /// <summary>Also cleared by M1-13 when the remembered member is removed.</summary>
    internal const string PrimaryMemberIdKey = "PrimaryCardiMemberId";

    /// <summary>
    /// One card per CardiMember on screen, by member id. The primary member's card is always
    /// first; the rest follow by first name (see <see cref="ArrangeCards"/>).
    /// </summary>
    private readonly Dictionary<Guid, MemberDashboardCard> _cards = [];

    /// <summary>The order the other members' cards go in, from the last member-list read.</summary>
    private IReadOnlyList<Guid> _otherMemberOrder = [];
    private const string VerifyEmailDismissedKey = "VerifyEmailNudgeDismissed";

    /// <summary>
    /// Holds the <see cref="HealthDataDisclosureScope"/> of the caregiver whose account confirmed
    /// the health-data disclosure was dismissed — a hint that stops the banner returning while
    /// the account cannot be asked, never the record itself. Scoped to the caregiver, so a
    /// session that expires without the Settings sign-out running cannot hand one person's
    /// acknowledgement to the next; sign-out clears it anyway, like <see cref="VerifyEmailDismissedKey"/>.
    /// </summary>
    internal const string HealthDataDisclosureConfirmedKey = "HealthDataDisclosureConfirmedFor";

    /// <summary>
    /// Holds the <see cref="TelemetryNotice"/> scope of the caregiver who acknowledged the
    /// telemetry notice on this phone. Per caregiver for the same reason as the disclosure hint;
    /// sign-out clears it too.
    /// </summary>
    internal const string TelemetryNoticeSeenKey = "TelemetryNoticeSeenFor";

    /// <summary>Set while the telemetry notice is up, so the OnAppearing its closing raises cannot open a second.</summary>
    private bool _telemetryNoticeOpen;
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(2);

    /// <summary>
    /// How long the hero card waits for the live status line before it admits to waiting — see
    /// <see cref="LoadCurrentStatusAsync"/>. Long enough that a cached answer never flashes a
    /// placeholder, short enough that a generation isn't left looking like a finished screen.
    /// </summary>
    private static readonly TimeSpan StatusLoadingThreshold = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How old a stored status line may be and still be put back on the card instead of the
    /// loading placeholder — see <see cref="RestoreStatusLineAsync"/>. Wide enough to cover a
    /// caregiver reopening the app through the day, short enough that what they read is still
    /// about today.
    /// </summary>
    private static readonly TimeSpan StatusLineRestoreWindow = TimeSpan.FromHours(6);

    /// <summary>Share of the row each Recent Alerts card takes in the carousel; see <see cref="SizeAlertCards"/>.</summary>
    private const double CarouselCardWidthFraction = 0.85;

    private readonly ICardiTrackApiClient _api;
    private readonly IAuthService _authService;
    private readonly IPopupService _popups;
    private readonly IStatusLineStore _statusLines;
    private readonly IQuestionValidityService _questionValidity;

    private enum DashboardState { Loading, Loaded, NoMember, Error }

    private bool _isSyncing;
    private bool _wizardActive;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private DashboardResponse? _lastData;

    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;

    /// <summary>
    /// How the last dashboard load ended — whether what is on screen reached the API or came
    /// off the device — for the paths that have to speak for that load rather than for whichever
    /// GET finished last (see <see cref="SyncAndReloadAsync"/>).
    /// </summary>
    private RefreshOutcome? _lastOutcome;

    public DashboardPage(
        ICardiTrackApiClient api,
        IAuthService authService,
        IPopupService popups,
        IStatusLineStore statusLines,
        IQuestionValidityService questionValidity)
    {
        InitializeComponent();
        _api = api;
        _authService = authService;
        _popups = popups;
        _statusLines = statusLines;
        _questionValidity = questionValidity;
        _feedback = new RefreshFeedback(SavedBanner, Updating);
        Header.BellTapped += OnBellClicked;
        DisclosureBanner.LearnMoreRequested += OnDisclosureLearnMore;
        DisclosureBanner.DismissRequested += OnDisclosureDismiss;
        AlertsHeader.SizeChanged += (_, _) => SizeAlertCards();
        NudgeHeader.SizeChanged += (_, _) => SizeNudgeRows();

        this.RefreshWhenAppResumes(RefreshUnattendedAsync);

        // A monitoring screen left open has to keep itself current. Until this, every refresh in
        // the app was edge-triggered — a caregiver watching the dashboard saw nothing move until
        // they pulled it down themselves.
        this.RefreshEvery(PeriodicRefresh.LiveDataInterval, RefreshUnattendedAsync);

        TabNavigation.DashboardExitArmed += OnDashboardExitArmed;
        Unloaded += (_, _) =>
        {
            TabNavigation.DashboardExitArmed -= OnDashboardExitArmed;
            _exitHintCts?.Cancel();
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        UpdateGreeting();
        UpdateVerifyEmailBanner();
        _ = RefreshDisclosureBannerAsync();
        _ = ShowTelemetryNoticeIfDueAsync();

        // Arriving on the screen is a pull, like the tick and the resume. This used to skip the
        // load when the last one was under a couple of minutes old, which meant a caregiver who
        // came here deliberately — the one moment they are certainly asking "how are they now?" —
        // could be shown a screen up to two minutes stale and no request in flight. The only gate
        // left is the shared MinimumGap floor, which exists to stop a load that has just run being
        // repeated: Android raises OnAppearing again on its way back to the foreground, where iOS
        // does not, so without it a resume would fetch twice on one platform and once on the other.
        _ = RefreshUnattendedAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        HideExitHint();
        TabNavigation.DisarmDashboardExit();
    }

    private CancellationTokenSource? _exitHintCts;

    private void OnDashboardExitArmed(object? sender, EventArgs e)
    {
        // A quick fade rather than a pop — the popup scrims' timing. Skipped when the hint is
        // already up (a third swipe inside the window) so re-arming doesn't blink it.
        if (!ExitHintBanner.IsVisible)
        {
            ExitHintScrim.Opacity = 0;
            ExitHintBanner.Opacity = 0;
            ExitHintScrim.IsVisible = true;
            ExitHintBanner.IsVisible = true;
            _ = ExitHintScrim.FadeToAsync(1, 140);
            _ = ExitHintBanner.FadeToAsync(1, 140);
        }

        _exitHintCts?.Cancel();
        _exitHintCts = new CancellationTokenSource();
        var ct = _exitHintCts.Token;
        _ = HideExitHintAfterAsync(ct);
    }

    private async Task HideExitHintAfterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(ExitConfirmation.Window, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        HideExitHint();
    }

    private void HideExitHint()
    {
        _exitHintCts?.Cancel();
        ExitHintBanner.IsVisible = false;
        ExitHintScrim.IsVisible = false;
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

    // Google-mandated health-data disclosure: shown until the account says it was dismissed. The
    // account is the record, asked live on every appearance — never the offline cache, since a
    // compliance banner decided by a stale answer is the wrong kind of last-known-good. When the
    // account cannot be asked, the banner shows: an unknown answer must read as "not yet told",
    // never as "acknowledged". The one thing kept on the device is that the account has
    // confirmed a dismissal, so a caregiver who has already read it is not shown it again every
    // time the phone is offline; sign-out clears it with the other per-device flags.
    private bool DisclosureConfirmedForCurrentCaregiver()
    {
        var scope = HealthDataDisclosureScope.For(_authService.CurrentUserEmail);
        return scope is not null
            && Preferences.Default.Get(HealthDataDisclosureConfirmedKey, string.Empty) == scope;
    }

    /// <summary>
    /// Tells the caregiver, once, that session telemetry is on and where to turn it off. A notice,
    /// not a choice: "Got it" or "Open Settings" both record it as seen — the second because the
    /// caregiver has plainly read it and is on their way to the switch, and a notice that met them
    /// again on the way back would be nagging. Back records nothing, so it returns next time.
    /// Waits while anything else is modal over the dashboard, or about to be — the device-setup
    /// wizard that can follow sign-in is pushed from the shell's Loaded event, which can arrive
    /// after this runs: it will come round on the next appearance instead.
    /// </summary>
    private async Task ShowTelemetryNoticeIfDueAsync()
    {
        try
        {
            var email = _authService.CurrentUserEmail;
            if (_telemetryNoticeOpen
                || _popups.IsShowing
                || Navigation.ModalStack.Count > 0
                || PostLoginRouter.DeviceSetupResumePending
                || TelemetryNotice.IsSeen(Preferences.Default.Get(TelemetryNoticeSeenKey, string.Empty), email))
                return;

            _telemetryNoticeOpen = true;
            bool? acknowledged;
            try
            {
                // Confirm is "Got it", cancel is "Open Settings"; null is Back.
                acknowledged = await _popups.AskInfoAsync(
                    TelemetryNotice.Message,
                    TelemetryNotice.Title,
                    confirmText: TelemetryNotice.AcknowledgeText,
                    cancelText: TelemetryNotice.OpenSettingsText);
            }
            finally
            {
                _telemetryNoticeOpen = false;
            }

            if (acknowledged is null)
                return;

            if (TelemetryNotice.SeenValueFor(email) is { } seen)
                Preferences.Default.Set(TelemetryNoticeSeenKey, seen);

            // GoToTabAsync, not the nav bar's route: this is a content link, so Back from Settings
            // owes the caregiver the dashboard they came from.
            if (acknowledged == false)
                await Shell.Current.GoToTabAsync(AppShell.SettingsRoute);
        }
        catch (Exception ex)
        {
            // Fire-and-forget from OnAppearing: a notice failing must not take the dashboard with it.
            Log.Warning(ex, "Showing the telemetry notice failed.");
        }
    }

    private void RememberDisclosureConfirmed()
    {
        if (HealthDataDisclosureScope.For(_authService.CurrentUserEmail) is { } scope)
            Preferences.Default.Set(HealthDataDisclosureConfirmedKey, scope);
    }

    private async Task RefreshDisclosureBannerAsync()
    {
        if (DisclosureConfirmedForCurrentCaregiver())
        {
            DisclosureBanner.IsVisible = false;
            return;
        }

        // Up before the answer is in: while the account has not yet said "dismissed", the notice
        // is owed, and a slow or absent network must not turn into its absence.
        DisclosureBanner.IsVisible = true;

        try
        {
            var disclosure = await _api.GetHealthDataDisclosureAsync();

            // A dismissal recorded while this was in flight wins over a stale "not yet" answer.
            if (disclosure.Dismissed || DisclosureConfirmedForCurrentCaregiver())
            {
                RememberDisclosureConfirmed();
                DisclosureBanner.IsVisible = false;
            }
        }
        catch (Exception)
        {
            // Unknown stays shown.
        }
    }

    private async void OnDisclosureLearnMore(object? sender, EventArgs e) =>
        await Navigation.PushModalAsync(new LegalDocumentPage(LegalDocumentPage.PrivacyTitle, LegalDocumentPage.PrivacyUrl));

    private async void OnDisclosureDismiss(object? sender, EventArgs e)
    {
        // Hide only once the dismissal has actually persisted — the same rule as the web banner.
        DisclosureBanner.SetBusy(true);
        try
        {
            await _api.DismissHealthDataDisclosureAsync();
            RememberDisclosureConfirmed();
            DisclosureBanner.IsVisible = false;
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't save that");
        }
        finally
        {
            DisclosureBanner.SetBusy(false);
        }
    }

    /// <summary>
    /// Short, quiet time-of-day line under the caregiver's own name — describes the caregiver's
    /// local evening, not the CardiMember's, so there's no cross-timezone reading to get wrong.
    /// Deliberately time-only: no weather/location signal exists anywhere in this app yet, so a
    /// "based on temperature" version is a separate, later feature, not a copy change here.
    /// </summary>
    private static string ContextLineFor(int hour) => hour switch
    {
        < 5 => "Hope you're getting some rest",
        < 8 => "Rise and shine",
        < 12 => "Hope your morning's off to a good start",
        < 17 => "Hope your afternoon is going well",
        < 21 => "Seems like a nice evening",
        _ => "Winding down for the night",
    };

    private void UpdateGreeting()
    {
        var timeOfDay = DateTime.Now.Hour switch
        {
            < 12 => "Good Morning",
            < 18 => "Good Afternoon",
            _ => "Good Evening",
        };
        var firstName = _authService.CurrentUserName?.Split(' ')[0];
        Header.SetGreeting(
            string.IsNullOrWhiteSpace(firstName) ? timeOfDay : firstName,
            ContextLineFor(DateTime.Now.Hour));
    }

    /// <summary>
    /// Color token for each <see cref="DashboardResponse.DataFreshness"/> tier. An
    /// unrecognised or empty value falls back to the neutral "unknown" color, not green — an
    /// unexpected value showing a reassuring color would be worse than showing none.
    /// </summary>

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
    /// The quiet reload behind all three unattended paths — arriving on the screen, the app
    /// returning to the foreground, and the timer ticking while the caregiver watches. All three
    /// share one floor, <see cref="ResumeRefresh.MinimumGap"/>, and nothing else: any longer
    /// window would hold back the very update the caregiver came to see.
    /// </summary>
    /// <remarks>
    /// A read, not a device sync: the server has been collecting from the wearable on its own —
    /// webhook-triggered within seconds, with the Worker's ten-minute poll as the fallback — so
    /// what is missing on screen is the fetch, not the collection. Asking the server to check in
    /// with the device on every foreground or tick would also earn the "too soon since the last
    /// check" refusal, and with it a popup for something nobody asked for. Only a deliberate pull
    /// or the refresh button syncs the device.
    /// </remarks>
    private Task RefreshUnattendedAsync() =>
        DateTime.UtcNow - _lastLoadedUtc < ResumeRefresh.MinimumGap
            ? Task.CompletedTask
            : LoadAsync(force: false);

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
        Refresher.IsRefreshing = true;

        string? syncError = null;
        try
        {
            // Every member on screen, not just the first: the pull is on the whole dashboard.
            // The first refusal is the one reported — they tend to share a reason, and a popup per
            // member would be a stack of the same sentence.
            foreach (var memberId in _cards.Keys.ToList())
            {
                try
                {
                    await _api.SyncDevicesAsync(memberId);
                }
                catch (ApiException ex)
                {
                    // Paused monitoring, no connected device, or too soon since the last check —
                    // each is the answer to "why hasn't this updated?", so none stays silent.
                    syncError ??= ex.Message;
                }
            }

            await LoadAsync(force: true);
        }
        finally
        {
            Refresher.IsRefreshing = false;
            _isSyncing = false;
        }

        // Asks about the reload just above, not about whichever GET happened to finish
        // last: the banner this defers to speaks for that same load. No load at all means
        // nothing is standing in for the sync error, so it is said.
        if (syncError is not null && _lastOutcome is not { Result: RefreshResult.SavedOnlyOffline })
            await _popups.ShowInfoAsync(syncError, "Couldn't check in");
    }

    private async Task LoadAsync(bool force)
    {
        // An unattended load — a tick, a resume, arriving on the screen — waits its turn behind
        // whatever is already running. A forced one supersedes it: the caregiver pulling the
        // screen down has just asked for the current state, and the device sync that ran before
        // this reload means the answer in flight is already the older one.
        if (_gate.IsLoading && !force)
            return;
        var ticket = _gate.Begin();

        if (_lastData is null)
            SetState(DashboardState.Loading);

        try
        {
            var memberId = await ResolveMemberIdAsync(force);
            if (!_gate.IsCurrent(ticket))
                return;
            if (memberId is null)
            {
                SetState(DashboardState.NoMember);
                return;
            }

            // On a landing with nothing on screen, the device's last dashboard for this member
            // goes up first and the live one replaces it under the overlay. A resume, a tick or a
            // pull already has a dashboard up and replaces it in place.
            var id = memberId.Value;
            var outcome = await SnapshotRefresh.RunAsync(
                _api, _gate, ticket,
                peek: _lastData is null ? ct => _api.PeekDashboardAsync(id, ct) : null,
                fetch: ct => _api.GetDashboardAsync(id, ct),
                render: data =>
                {
                    Apply(data);

                    // Committed only once it is actually on screen. Everything below reads
                    // _lastData as "there is already a dashboard here worth keeping", which is
                    // only true after Apply has run: assigning it first meant a fault part-way
                    // through Apply left the field set over a screen that had never been filled
                    // in, and the error paths then protected a skeleton instead of replacing it.
                    _lastData = data;
                    SetState(DashboardState.Loaded);
                },
                _feedback);

            switch (outcome.Result)
            {
                case RefreshResult.Superseded:
                    // Recorded only for a load that still owns the screen. A superseded one
                    // writing here would let the older answer speak for the newer load — and
                    // SyncAndReloadAsync reads this to decide whether to report a sync refusal.
                    return;
                case RefreshResult.NothingAndFailed:
                    // Nothing to show — or a 404 over a snapshot, which means the member is gone
                    // and their saved dashboard must not stand in for them.
                    _lastOutcome = outcome;
                    _lastData = null;
                    ErrorDetailLabel.Text = outcome.Error!.Message;
                    SetState(DashboardState.Error);
                    return;
            }

            _lastOutcome = outcome;
            ApplyStaleBanner(_lastData!, outcome);

            // Everyone else the family watches, after the primary member is on screen — never
            // before it, and never holding it up. Awaited so a pull's spinner covers them too.
            await LoadOtherMembersAsync(id, liveOnly: !outcome.IsFresh);

            if (!outcome.IsFresh)
            {
                // Saved data is on screen and the banner says so. Nothing more is asked of the
                // server: a status line generated over a dashboard the API could not serve would
                // be a guess dressed as a reading, and a failed refresh over existing data stays
                // quiet — that beats blanking the dashboard.
                return;
            }

            _lastLoadedUtc = DateTime.UtcNow;

            // Fire-and-forget, not awaited: the hero card already shows its static per-tier
            // copy, and a MedGemma call can take a few seconds — nothing about the dashboard
            // should wait on it, including the pull-to-refresh spinner below.
            _ = LoadCurrentStatusAsync(CardFor(_lastData!.CardiMemberId), _lastData!);

            // Loaded after the dashboard rather than alongside it: a caregiver opens this screen
            // to see how their relative is, and housekeeping must never delay that answer or take
            // it down with it.
            await LoadNudgesAsync();
        }
        catch (ApiException ex)
        {
            // The member list behind ResolveMemberIdAsync failing; the dashboard read itself
            // reports through its outcome above.
            if (_lastData is null)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(DashboardState.Error);
            }
        }
        catch (Exception ex)
        {
            // Anything that is not the API answering badly — a fault while putting the data on
            // screen, most likely. This catch exists because without it such a fault is silent and
            // permanent: it escapes into a fire-and-forget task with nothing observing it, the page
            // never reaches Loaded, and every retry meets the same data and fails the same way, so
            // the caregiver is left watching loading placeholders for the rest of the session with
            // nothing to tap. A monitoring screen may fail, but it has to admit that it failed.
            ScreenRefresh.LogFailure(ex, this, "while loading");
            if (_lastData is null)
            {
                ErrorDetailLabel.Text = "Something went wrong while showing this dashboard.";
                SetState(DashboardState.Error);
            }
        }
        finally
        {
            _gate.Release(ticket);
        }
    }

    private async Task<Guid?> ResolveMemberIdAsync(bool force)
    {
        var cached = Preferences.Default.Get(PrimaryMemberIdKey, string.Empty);
        var remembered = Guid.TryParse(cached, out var cachedId) ? cachedId : (Guid?)null;
        if (!force && remembered is { } id)
            return id;

        // A forced resolve re-reads the list, but it does not get to change the subject: the
        // remembered member is still the one being shown, and it is only given up when the list
        // no longer has it. Without this the refresh that follows a pull, a questionnaire answer
        // or the wizard silently moves the dashboard to whichever member happens to sort first,
        // and then writes that choice to PrimaryMemberIdKey, so it sticks.
        var primary = PrimaryCardiMember.From(await _api.GetCardiMembersAsync(), remembered);
        if (primary is null)
        {
            Preferences.Default.Remove(PrimaryMemberIdKey);
            return null;
        }

        Preferences.Default.Set(PrimaryMemberIdKey, primary.Id.ToString());
        return primary.Id;
    }

    /// <summary>
    /// Draws the primary member's dashboard into their card, and the parts of the page that belong
    /// to the whole family: the chat launcher, the bell's count and the alert strip.
    /// </summary>
    private void Apply(DashboardResponse data)
    {
        ChatBot.MemberId = data.CardiMemberId;
        ChatBot.MemberFirstName = data.DisplayFirstName();

        CardFor(data.CardiMemberId).Apply(data, _popups);
        ArrangeCards(data.CardiMemberId);
        ApplyAlerts();
    }

    /// <summary>The card for a member, made and wired the first time it is asked for.</summary>
    private MemberDashboardCard CardFor(Guid memberId)
    {
        if (_cards.TryGetValue(memberId, out var existing))
            return existing;

        var card = new MemberDashboardCard();
        card.DetailsRequested += (_, _) => OpenMemberDetails(card);
        card.AdviseRequested += (_, _) => OpenAdvise(card);
        card.AlertsRequested += async (_, _) => await OpenMemberAlertsAsync(card);
        card.DaybookRequested += async (_, _) => await OpenDaybookAsync(card);
        card.NoDeviceRequested += async (_, _) => await OfferConnectAsync(card);
        card.QuestionRequested += async (_, _) => await AnswerPendingQuestionAsync(card);
        card.WeatherRequested += async (_, weather) => await _popups.ShowWeatherAsync(weather);
        card.SleepAlertRequested += async (_, alertId) =>
            await Shell.Current.GoToAsync($"{AlertDetailPage.Route}?alertId={alertId}");
        _cards[memberId] = card;
        return card;
    }

    /// <summary>
    /// Puts the cards in order — the primary member first, the rest by first name — and marks the
    /// primary once there is more than one member to tell it apart from.
    /// </summary>
    private void ArrangeCards(Guid primaryId)
    {
        var ordered = new List<MemberDashboardCard>();
        if (_cards.TryGetValue(primaryId, out var primary))
            ordered.Add(primary);
        foreach (var id in _otherMemberOrder)
        {
            if (id != primaryId && _cards.TryGetValue(id, out var card) && card.Data is not null)
                ordered.Add(card);
        }

        var several = ordered.Count > 1;
        foreach (var card in ordered)
            card.SetPrimary(several && card == primary);

        if (MemberCards.Children.SequenceEqual(ordered))
            return;
        MemberCards.Clear();
        foreach (var card in ordered)
            MemberCards.Add(card);
    }

    /// <summary>
    /// Every member other than the primary one: their saved dashboard first when their card is
    /// empty, then the live one. Loaded side by side — one slow member must not hold up the next.
    /// </summary>
    /// <param name="liveOnly">
    /// True when the primary member's own load could not reach the server: the others are shown
    /// from what the phone saved, and nothing live is asked for.
    /// </param>
    private async Task LoadOtherMembersAsync(Guid primaryId, bool liveOnly)
    {
        List<CardiMemberResponse> members;
        try
        {
            members = await _api.GetCardiMembersAsync();
        }
        catch (ApiException)
        {
            // The list is what says who else there is. Without it the cards already on screen
            // stay as they are; the primary member's is the one that matters most and is up.
            return;
        }

        var others = members
            .Where(m => m.Id != primaryId)
            .OrderBy(m => m.DisplayFirstName(), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _otherMemberOrder = others.Select(m => m.Id).ToList();

        // A member who has left the family (removed, or access taken away) leaves the screen.
        foreach (var gone in _cards.Keys.Where(id => id != primaryId && !_otherMemberOrder.Contains(id)).ToList())
            _cards.Remove(gone);

        await Task.WhenAll(others.Select(m => LoadOtherMemberAsync(m.Id, liveOnly)));
        ArrangeCards(primaryId);
        ApplyAlerts();
    }

    private async Task LoadOtherMemberAsync(Guid memberId, bool liveOnly)
    {
        var card = CardFor(memberId);
        try
        {
            if (card.Data is null && await _api.PeekDashboardAsync(memberId) is { } saved)
                card.Apply(saved, _popups);

            if (liveOnly)
                return;

            var live = await _api.GetDashboardAsync(memberId);
            card.Apply(live, _popups);
            _ = LoadCurrentStatusAsync(card, live);
        }
        catch (ApiException ex) when (ex.IsNotFound)
        {
            // Gone between the list and the read: their saved dashboard must not stand in for them.
            _cards.Remove(memberId);
        }
        catch (ApiException)
        {
            // Unreachable: a card already showing saved data keeps it, and one with nothing to
            // show is left out by ArrangeCards rather than drawn empty.
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, this, "while loading another member");
        }
    }

    /// <summary>
    /// The Recent Alerts card, for everyone on screen: the members' strips merged, newest first,
    /// each naming its member once there is more than one. A lone alert gets the card's full
    /// width; two or more go into the carousel, sized by <see cref="SizeAlertCards"/>.
    /// </summary>
    /// <remarks>
    /// "View all" carries the count only when the strip is short of it. Each strip and its
    /// <see cref="DashboardResponse.UnreadAlertCount"/> are the same set — unacknowledged,
    /// unresolved — but the server caps each strip, so the counts are added up rather than the
    /// cards counted. The bell carries the same family-wide total.
    /// </remarks>
    private void ApplyAlerts()
    {
        SingleAlertHost.Content = null;
        AlertsStack.Clear();

        var shown = MemberCards.Children.OfType<MemberDashboardCard>()
            .Select(c => c.Data)
            .OfType<DashboardResponse>()
            .ToList();
        var named = shown.Count > 1;
        var alerts = shown
            .SelectMany(d => d.RecentAlerts.Select(a => (Alert: a, Name: named ? d.DisplayFirstName() : null)))
            .OrderByDescending(x => x.Alert.TriggeredAt)
            .ToList();
        var unread = shown.Sum(d => d.UnreadAlertCount);

        Header.SetUnreadCount(unread);
        AlertsSection.IsVisible = alerts.Count > 0;
        SingleAlertHost.IsVisible = alerts.Count == 1;
        AlertsScroller.IsVisible = alerts.Count > 1;

        ViewAllAlertsLink.Text = unread > alerts.Count
            ? $"View all ({unread})"
            : "View all";

        foreach (var (alert, name) in alerts)
        {
            var card = new AlertMiniCard();
            card.Apply(alert, name);
            card.AlertTapped += OnAlertTapped;

            if (alerts.Count == 1)
                SingleAlertHost.Content = card;
            else
                AlertsStack.Add(card);
        }

        SizeAlertCards();
    }

    /// <summary>
    /// Sizes the carousel's cards to most of the card's content width, so one is read at a time
    /// and the edge of the next says the row scrolls. Measured off the heading, since the
    /// carousel itself runs edge to edge and is wider than the content. Re-run whenever the
    /// heading is resized, since the first <see cref="ApplyAlerts"/> can land before it has been
    /// measured.
    /// </summary>
    private void SizeAlertCards()
    {
        if (AlertsHeader.Width <= 0)
            return;

        var width = Math.Floor(AlertsHeader.Width * CarouselCardWidthFraction);
        foreach (var card in AlertsStack.Children.OfType<AlertMiniCard>())
            card.WidthRequest = width;
    }

    /// <summary>
    /// The stale banner (M1-09c), decided once the load has ended rather than inside
    /// <see cref="Apply"/>, because it depends on how the load ended. Suppressed while paused:
    /// data is meant to be stale then, and "pull down to check in" would be advice we can't
    /// honour. Suppressed while what is on screen is saved data: the saved-data banner already
    /// says it is last-known-good, and a pull cannot reach the server that banner says is out of
    /// reach.
    /// </summary>
    private void ApplyStaleBanner(DashboardResponse data, RefreshOutcome outcome)
    {
        var isStale = !data.MonitoringPaused
            && outcome.IsFresh
            && data.LastSyncedAt is { } synced
            && DateTime.UtcNow - DateTime.SpecifyKind(synced, DateTimeKind.Utc) > StaleThreshold;
        var wasStale = StaleBanner.IsVisible;
        StaleBanner.IsVisible = isStale;
        if (isStale)
        {
            StaleBannerLabel.Text =
                $"Last update was {RelativeTime.Format(data.LastSyncedAt!.Value)} — pull down to check in";

            // Only fade on the transition into "stale" — re-applying the same state on every
            // auto-refresh would otherwise re-fade a banner that's already visible.
            // Already-stale still forces full opacity rather than leaving it untouched: a fade
            // interrupted by the app backgrounding mid-animation would otherwise strand the
            // banner semi-transparent until it leaves and re-enters the stale state.
            if (!wasStale)
            {
                StaleBanner.Opacity = 0;
                _ = StaleBanner.FadeToAsync(1, 180, Easing.CubicOut);
            }
            else
            {
                StaleBanner.Opacity = 1;
            }
        }
        else
        {
            StaleBanner.Opacity = 0;
        }
    }

    private void SetState(DashboardState state)
    {
        SkeletonPanel.IsVisible = state == DashboardState.Loading;
        ContentPanel.IsVisible = state == DashboardState.Loaded;
        NoMemberPanel.IsVisible = state == DashboardState.NoMember;
        ErrorPanel.IsVisible = state == DashboardState.Error;
    }

    /// <summary>
    /// Both the hero card and the quick-action row's Details tile land on M1-13, for the member
    /// whose card was tapped.
    /// </summary>
    private static void OpenMemberDetails(MemberDashboardCard card)
    {
        if (card.Data is not { } data)
            return;
        _ = Shell.Current.GoToAsync($"{CardiMemberDetailPage.Route}?memberId={data.CardiMemberId}");
    }

    /// <summary>
    /// The card's Advise button — M1-13, opened at the "Something to try" suggestion rather than
    /// at the top. The button only exists while there is one to read.
    /// </summary>
    private static void OpenAdvise(MemberDashboardCard card)
    {
        if (card.Data is not { } data)
            return;
        _ = Shell.Current.GoToAsync(
            $"{CardiMemberDetailPage.Route}?memberId={data.CardiMemberId}" +
            $"&focus={CardiMemberDetailPage.AdviseFocus}");
    }

    /// <summary>
    /// The bell is the way in to everything wanting attention. It opens the alerts list, which
    /// carries completeness items in their own section below the health ones — sectioned, never
    /// interleaved, so scanning for a health event does not mean wading through housekeeping.
    /// </summary>
    private async void OnBellClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToTabAsync(AppShell.AlertsRoute);

    /// <summary>
    /// Lands on M1-10, which is still a placeholder. The link renders anyway: hiding it would
    /// make the screen diverge from the design for a reason no caregiver can see, and the
    /// placeholder is the more honest signal that alerting is not finished.
    /// </summary>

    /// <summary>
    /// The member card's Alerts button — the same origin-remembering jump as View All, but
    /// narrowed to this CardiMember: it is on their card, under their name. The name travels with
    /// the id purely so the chip on the other side can say whose list this is.
    /// </summary>
    private static async Task OpenMemberAlertsAsync(MemberDashboardCard card)
    {
        if (card.Data is not { } data)
        {
            await Shell.Current.GoToTabAsync(AppShell.AlertsRoute);
            return;
        }

        var name = data.DisplayFirstName();
        var route = $"{AppShell.AlertsRoute}?memberId={data.CardiMemberId}";
        if (!string.IsNullOrWhiteSpace(name))
            route += $"&memberName={Uri.EscapeDataString(name)}";

        await Shell.Current.GoToTabAsync(route);
    }

    /// <summary>
    /// The member card's no-device button: the no-device card (Figma M1-09 D) as a modal, and the
    /// connect flow for that member when the caregiver taps Connect in it.
    /// </summary>
    private async Task OfferConnectAsync(MemberDashboardCard card)
    {
        if (card.Data is not { } data)
            return;

        if (await _popups.ShowNoDeviceAsync(data.DisplayFirstName()))
            await ConnectDeviceAsync(data.CardiMemberId);
    }

    private async void OnViewAllAlertsTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToTabAsync(AppShell.AlertsRoute);

    /// <summary>The member card's link to their finished days — the Daybook tab, filtered to
    /// them, arriving through the same origin-remembering jump every content affordance uses.</summary>
    private static async Task OpenDaybookAsync(MemberDashboardCard card)
    {
        if (card.Data is not { } data)
            return;

        await Shell.Current.GoToTabAsync($"{AppShell.JournalRoute}?memberId={data.CardiMemberId}");
    }

    /// <summary>
    /// The hero card's Q&amp;A button — opens the pending question as a modal, and applies
    /// whatever the caregiver did with it. Same validity re-check and lapsed-question copy
    /// QuestionnairesPage's own pending card uses: the badge that made this button visible was
    /// drawn from the last load, and the question can have lapsed in the time since.
    /// </summary>
    private async Task AnswerPendingQuestionAsync(MemberDashboardCard card)
    {
        if (card.Data is not { PendingQuestionnaire: { } pending } data)
            return;

        var verified = _questionValidity.Verify(pending);
        if (verified is null)
        {
            await _popups.ShowInfoAsync(
                "That one was about a day that's now over, so we've let it go. We'll ask again if "
                + "it still matters.",
                "This question has passed");
            await LoadAsync(force: true);
            return;
        }

        var result = await _popups.ShowPendingQuestionAsync(
            verified, data.DisplayFirstName());

        switch (result.Outcome)
        {
            case QuestionPopupOutcome.Answered when result.Answer is { } answer:
                try
                {
                    await _api.AnswerQuestionnaireAsync(
                        verified.Id, new AnswerQuestionnaireRequest { AnswerText = answer });
                    await LoadAsync(force: true);
                }
                catch (ApiException ex) when (!ex.IsSessionExpired)
                {
                    await _popups.ShowWarningAsync(ex.Message, "Couldn't save your answer");
                }
                break;

            case QuestionPopupOutcome.Dismissed:
                try
                {
                    await _api.DismissQuestionnaireAsync(verified.Id);
                    await LoadAsync(force: true);
                }
                catch (ApiException ex) when (!ex.IsSessionExpired)
                {
                    await _popups.ShowWarningAsync(ex.Message, "Couldn't skip that question");
                }
                break;
        }
    }

    /// <summary>A dashboard recent-alert tile opens the matching detail screen.</summary>
    private async void OnAlertTapped(object? sender, Guid alertId)
    {
        if (alertId == Guid.Empty)
            return;
        await Shell.Current.GoToAsync($"{AlertDetailPage.Route}?alertId={alertId}");
    }

    private async void OnAddMemberClicked(object? sender, EventArgs e)
    {
        if (_wizardActive)
            return;
        _wizardActive = true;
        try
        {
            var result = await WizardLauncher.RunModalAsync(Navigation, member: null);
            // "Go to Dashboard" has already replaced this page with a fresh shell —
            // reloading here would fetch for a page that is gone.
            if (result.ExitedToDashboard)
                return;
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

    /// <summary>Runs the connect flow for the member whose card asked for it.</summary>
    private async Task ConnectDeviceAsync(Guid memberId)
    {
        if (_wizardActive)
            return;
        _wizardActive = true;
        try
        {
            // One round trip: the members list answers both "which member" and "is there one".
            var member = (await _api.GetCardiMembersAsync()).FirstOrDefault(m => m.Id == memberId);
            if (member is null)
                return;

            var result = await WizardLauncher.RunModalAsync(Navigation, member);
            if (result.ExitedToDashboard)
                return;
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

    /// <summary>
    /// A live, empathetic replacement for the hero card's static status line. Best-effort: the
    /// static copy <see cref="Apply"/> already rendered is a complete, correct fallback, and every
    /// path that does not produce a live line puts it back.
    /// </summary>
    private async Task LoadCurrentStatusAsync(MemberDashboardCard card, DashboardResponse data)
    {
        var heroCard = card.HeroCard;
        // Neither tier calls the model: a paused member has no reading to interpret, and one with
        // no baseline yet already shows the day's own numbers. Returning here is what keeps them
        // off the loading line below, which they would otherwise never leave.
        if (data.HealthStatus is "unknown" or "paused")
            return;

        var pending = _api.GetCurrentStatusAsync(data.CardiMemberId);

        // Put back the line this member last had before deciding whether to admit to waiting.
        // The card's live line lives in fields on the control, so it dies with the page — and the
        // page is transient behind a tab template, which made every cold start look like a first
        // load and sent a caregiver reopening the app to the placeholder even though the answer
        // was already on the device. Restoring first means the gate below sees a live line and
        // leaves it alone; the refresh already in flight replaces it in place a moment later.
        if (!heroCard.HasLiveStatusFor(data.CardiMemberId, data.HealthStatus))
            await RestoreStatusLineAsync(heroCard, data);

        // Only say "Loading" once the wait is long enough to be worth admitting to.
        //
        // This used to blank the card the moment the call started, on the reasoning that showing
        // the per-tier copy as though it were the answer and swapping it under the reader a few
        // seconds later was the worse of the two. That reasoning holds for a slow call and not for
        // a quick one, and the quick one is the ordinary case — the server caches this line for
        // minutes, so most loads answer from cache in well under a second. What the unconditional
        // version cost was the cold path: the generation runs to a 25-second server budget, and a
        // caregiver opening the dashboard could sit on "Please wait — checking how they're doing"
        // for all of it, with the tier's own perfectly good sentence withheld the whole time.
        //
        // Waiting the threshold gets both: a cached answer goes straight from the static line to
        // the live one with no placeholder in between, and a generation still says what it is
        // doing rather than leaving a stale-looking sentence to be replaced without warning.
        //
        // Skipped when the card already shows a live line for this member and tier — an unattended
        // tick would otherwise blank a good line to re-fetch the same words.
        if (!heroCard.HasLiveStatusFor(data.CardiMemberId, data.HealthStatus)
            && await Task.WhenAny(pending, Task.Delay(StatusLoadingThreshold)) != pending)
        {
            heroCard.ShowStatusLoading();
        }

        try
        {
            var status = await pending;
            if (status.Message is { } message)
            {
                heroCard.ApplyDynamicMessage(
                    status.Headline, message, data.CardiMemberId, data.HealthStatus);

                // Kept with the tier it describes, so the next cold start can tell whether it is
                // still about the day on screen.
                await _statusLines.SaveAsync(
                    data.CardiMemberId,
                    new StoredStatusLine(data.HealthStatus, status.Headline, message, status.GeneratedAt));
            }
            else
            {
                // No answer right now — which is not the same as "the sentence is retired", and
                // this branch used to read it as the latter: it cleared the live line, re-applied
                // the static copy, and deleted the stored one.
                //
                // The server answers with a null Message for reasons that are all temporary. It
                // damps fan-out with a per-member generation claim held for the whole 25-second
                // budget, and every other caller inside that window is told there is nothing to
                // say; a generation that runs past the budget answers the same way. The result is
                // only cached at the very end, so the window is wide open — and the dashboard is
                // entered from three places in quick succession (cold start, tab switch, resume
                // fan-out), so one entry generates while the next loses the claim.
                //
                // Deleting the stored line on that answer emptied the store faster than a
                // successful generation could fill it, which left the restore with nothing to put
                // back and sent the card to "Loading — please wait" on every single entry: the
                // exact placeholder the store exists to prevent.
                //
                // So: leave the card alone. It is already showing something correct — the line
                // restored from the device, or the tier's own copy from Apply — and the stored
                // line stays for the tier gate and the six-hour window to retire on their own
                // terms. A member who has genuinely stopped having anything to say has moved to
                // a tier this method returns on before the call is even made.
            }
        }
        catch (ApiException)
        {
            // Put the static copy back: the card may be showing "Loading", and leaving it there
            // would turn a failed side-call into a screen that never resolves. Harmless when it
            // isn't — Apply re-renders the same tier, and restores the live line if one survived,
            // which now includes a line restored from the device a moment ago.
            heroCard.Apply(data);
            // Static per-tier copy stays. Nothing to show the caregiver about this failure —
            // it isn't actionable and isn't worth interrupting them for.
        }
    }

    /// <summary>
    /// Shows the last status line saved for this member, if one is recent enough and was written
    /// about the tier now on screen. Best-effort in every direction: no stored line, a stale one,
    /// or a store that cannot be read all leave the card exactly as <see cref="Apply"/> rendered it.
    /// </summary>
    private async Task RestoreStatusLineAsync(StatusHeroCard heroCard, DashboardResponse data)
    {
        StoredStatusLine? stored;
        try
        {
            stored = await _statusLines.TryGetAsync(
                data.CardiMemberId, data.HealthStatus, StatusLineRestoreWindow);
        }
        catch (Exception ex)
        {
            // The store swallows its own I/O failures; this is the belt-and-braces catch for
            // anything it doesn't. A dashboard must not fail over a cosmetic read.
            ScreenRefresh.LogFailure(ex, this, "while restoring the saved status line");
            return;
        }

        if (stored is not null)
        {
            heroCard.ApplyDynamicMessage(
                stored.Headline, stored.Message, data.CardiMemberId, data.HealthStatus);
        }
    }

    // ------------------------------------------------------------------ data-completeness nudges

    /// <summary>
    /// Fills the safety banners and the two "Complete the picture" slots.
    /// </summary>
    /// <remarks>
    /// Failures are swallowed on purpose. This is the housekeeping strip on a health screen — if
    /// the summary call fails, the right outcome is a dashboard without it, not an error dialog
    /// over the metrics somebody actually came to read.
    /// </remarks>
    private async Task LoadNudgesAsync()
    {
        try
        {
            var summary = await _api.GetNotificationSummaryAsync();
            RenderNudges(summary, summary.DashboardCards);

            // The summary carries the top two. When more are waiting, the rest come from the
            // inbox's own list so the row can scroll through all of them — after the two, which
            // keep their places: the summary ranks by priority and the list is the inbox's order.
            if (WaitingNudges(summary) > summary.DashboardCards.Count)
            {
                var open = await _api.GetNotificationsAsync(state: nameof(NotificationState.Open), owned: true);
                var shown = summary.DashboardCards.Select(card => card.Id).ToHashSet();
                // The list's total, not the summary's count, is how many are waiting: the summary
                // counts only the top items it projects, and the list is one page of the inbox, so
                // either can stop short of the real number. Safety items are in the total and not
                // in this row.
                RenderNudges(summary,
                [
                    .. summary.DashboardCards,
                    .. open.Items.Where(n => n.Category != NotificationCategory.Safety && !shown.Contains(n.Id)),
                ],
                waiting: Math.Max(0, open.TotalCount - summary.SafetyBanners.Count));
            }
        }
        catch (ApiException)
        {
            // The top two may already be on screen; losing the rest leaves them there rather than
            // blanking a card that was fine a moment ago. Only a failed summary hides everything.
            if (!CompleteThePictureCard.IsVisible)
            {
                SafetyBannerList.IsVisible = false;
                Header.SetNudgeIndicator(false);
            }
        }
    }

    /// <summary>Open items for the "Complete the picture" card — everything but the safety banners.</summary>
    private static int WaitingNudges(NotificationSummaryResponse summary) =>
        Math.Max(0, summary.OpenCount - summary.SafetyBanners.Count);

    /// <param name="waiting">
    /// How many items are open for this card in all, when known better than the summary knows it —
    /// see <see cref="LoadNudgesAsync"/>. Defaults to the summary's own count.
    /// </param>
    private void RenderNudges(
        NotificationSummaryResponse summary, IReadOnlyList<NotificationResponse> cards, int? waiting = null)
    {
        SafetyBannerList.Clear();
        NudgeList.Clear();
        SingleNudgeHost.Content = null;

        foreach (var banner in summary.SafetyBanners)
        {
            var row = new NudgeMiniRow(banner, asSafetyBanner: true);
            row.Tapped += OnNudgeTapped;
            SafetyBannerList.Add(row);
        }

        // One item fills the card; two or more scroll sideways — see SizeNudgeRows.
        foreach (var card in cards)
        {
            var row = new NudgeMiniRow(card);
            row.Tapped += OnNudgeTapped;
            if (cards.Count == 1)
                SingleNudgeHost.Content = row;
            else
                NudgeList.Add(row);
        }
        SingleNudgeHost.IsVisible = cards.Count == 1;
        NudgeScroller.IsVisible = cards.Count > 1;
        SizeNudgeRows();

        SafetyBannerList.IsVisible = summary.SafetyBanners.Count > 0;
        CompleteThePictureCard.IsVisible = cards.Count > 0;
        Header.SetNudgeIndicator(summary.OpenCount > 0);

        // How many are waiting, on the title, so a caregiver knows there is more than the one in
        // view before they swipe. Not on a lone item — "1" beside a single card is the card again.
        var total = Math.Max(waiting ?? WaitingNudges(summary), cards.Count);
        NudgeCountBadge.IsVisible = total > 1;
        NudgeCountLabel.Text = total > 9 ? "9+" : total.ToString(CultureInfo.CurrentCulture);
        SemanticProperties.SetDescription(NudgeCountBadge, $"{total} to complete");

        // The link is only worth offering when there is more behind it than the row can show.
        CompleteThePictureLink.IsVisible = total > cards.Count;
    }

    /// <summary>
    /// Sizes the "Complete the picture" row's items to most of the card's content width, the
    /// Recent Alerts carousel's rule, so the next item peeks in at the edge. Measured off the
    /// heading, since the row itself runs edge to edge.
    /// </summary>
    private void SizeNudgeRows()
    {
        if (NudgeHeader.Width <= 0)
            return;

        var width = Math.Floor(NudgeHeader.Width * CarouselCardWidthFraction);
        foreach (var row in NudgeList.Children.OfType<NudgeMiniRow>())
            row.WidthRequest = width;
    }

    private async void OnNudgeTapped(object? sender, NotificationResponse notification) =>
        await Shell.Current.GoToAsync(NotificationsPage.Route);

    private async void OnSeeAllNudgesTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(NotificationsPage.Route);
}
