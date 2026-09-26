using System.Globalization;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Family;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Core.Navigation;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Core.Questionnaires;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// M1-13 CardiMember Detail. Entered from the dashboard hero card or its "View Details"
/// action, and re-entered after M1-14/M1-15 so edits show up immediately.
/// </summary>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(FocusSection), "focus")]
public partial class CardiMemberDetailPage : ContentPage
{
    /// <summary>Shell route; see <see cref="AppShell"/>.</summary>
    public const string Route = "memberdetail";

    /// <summary>
    /// <c>?focus=</c> value that opens this page at the "Something to try" (Advise) card rather than at
    /// the top — what the Dashboard card's Advise button navigates with, so the pulse a caregiver
    /// tapped lands on the suggestion it was pulsing about instead of somewhere down a long page.
    /// </summary>
    public const string AdviseFocus = "advise";

    private static readonly (string Label, int Hours)[] PauseDurations =
    [
        ("24 hours", 24),
        ("48 hours", 48),
        ("3 days", 72),
        ("1 week", 168),
    ];

    /// <summary>Every metric the carousel swipes through — see <see cref="TrendMetricCatalogue"/>.</summary>
    private static IReadOnlyList<TrendMetricCatalogue.Entry> TrendCards => TrendMetricCatalogue.All;

    /// <summary>
    /// The render gate for a reload made from the page while the caregiver is looking at it —
    /// the member is already up, so there is nothing to wait for. See the gate's own comment in
    /// <see cref="LoadAsync"/> for what the loads use it to avoid.
    /// </summary>
    private static readonly Task<bool> AlreadyOnScreen = Task.FromResult(true);

    /// <summary>
    /// Why a load is happening. Two independent things hang off it, which is why it is not a
    /// bool: whether a failure is worth interrupting the caregiver about, and whether this counts
    /// as asking for the generated cards again.
    /// </summary>
    private enum LoadTrigger
    {
        /// <summary>
        /// Landing on the screen. The member is always refetched — coming back from the edit
        /// screen or device management, the cached copy is exactly the thing that just changed —
        /// but navigation is not a request for the generated cards, so their cadence stands.
        /// </summary>
        Arrival,

        /// <summary>
        /// A pull, the retry button, or a reload after something the caregiver just did. Reads
        /// everything, whatever the clock says: someone who pulls the screen down is entitled to
        /// be told that nothing moved.
        /// </summary>
        Requested,

        /// <summary>
        /// The timer and the app resume. Nobody asked, so a failure passes without a popup.
        /// </summary>
        Unattended,
    }

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly IQuestionValidityService _questionValidity;
    private readonly IGeneratedContentSchedule _schedule;

    private readonly List<MetricTrend> _trends = [];
    private readonly List<BoxView> _trendIndicators = [];
    private readonly List<BoxView> _contactIndicators = [];
    private readonly List<ContactCardItem> _contacts =
    [
        new() { Kind = ContactCardItem.Emergency, Title = "Emergency Contact" },
        new() { Kind = ContactCardItem.Phone, Title = "Phone" },
    ];
    private bool _contactsBound;

    private readonly MemberRoute _route = new();

    /// <summary>
    /// Set by <see cref="FocusSection"/>, consumed by the first <see cref="LoadAsync"/> after
    /// it. One arrival, not a standing preference: the page reloads itself every thirty seconds,
    /// and a flag left standing would haul a caregiver back to the suggestion each time.
    /// </summary>
    private bool _focusAdvise;

    private bool _isBusy;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private CardiMemberDetailResponse? _member;

    private readonly LoadGate _gate = new();

    /// <summary>
    /// The order of the follow-up loads — the summary, the suggestion and the question. Not the
    /// gate above: that one releases when the member load settles, and these three are routinely
    /// still in flight at that moment, so two passes can have reads out for the same card at once
    /// and the older can land last (#1105). See <see cref="FollowUpGate"/> for why this is a bare
    /// counter and why it is kept per card.
    /// </summary>
    private readonly FollowUpGate _followUps = new();

    private readonly RefreshFeedback _feedback;

    /// <summary>
    /// Whether a generated summary is currently on screen. Guards the placeholder — see
    /// <see cref="Apply"/>.
    /// </summary>
    private bool _digestRendered;

    /// <summary>
    /// The digest the urgency rung and the status note on screen were drawn from, or null when
    /// this member has none yet. Held rather than used and dropped because both of those read
    /// the member as well as the digest: <see cref="Apply"/> lands fresh member data on every
    /// refresh, and without the digest beside it neither could be recomputed there — so a note
    /// naming an alert that had since been resolved stayed on screen until a *later* digest call
    /// happened to succeed, and outlived the alert entirely when none did.
    /// </summary>
    private DigestResponse? _digest;

    /// <summary>Open/close timing of the pause-duration drop down, matching AccordionSection.</summary>
    private const uint PauseDropdownMs = 200;

    private const string PauseDropdownAnimation = "pauseDropdown";

    private bool _pauseDurationsOpen;
    private bool _pauseDurationsAnimating;

    /// <summary>
    /// Whether the last thing to take the screen from this page was one of our own popups — see
    /// <see cref="OnDisappearing"/>.
    /// </summary>
    private bool _returningFromPopup;

    /// <summary>
    /// Set on the way to a screen that can answer or dismiss this member's questions, and
    /// consumed by the load that follows the way back. The questions screen is pushed over this
    /// page, so returning finds the same instance with the old card still up, and the mutation
    /// evicted the saved copy — leaving the cadence nothing to fall back on and no reason to go
    /// and look.
    /// </summary>
    private bool _questionsChangedElsewhere;

    public CardiMemberDetailPage(
        ICardiTrackApiClient api,
        IPopupService popups,
        IQuestionValidityService questionValidity,
        IGeneratedContentSchedule schedule)
    {
        InitializeComponent();
        this.HoldUntilInsetsApplied();
        _api = api;
        _popups = popups;
        _questionValidity = questionValidity;
        _schedule = schedule;
        _feedback = new RefreshFeedback(SavedBanner, Updating);
        BuildPauseDurations();
        InsightMovementCards.OnCreateAlert = OpenAlertForMovementAsync;
        PendingQuestionCard.AnswerSubmitted += OnQuestionAnswered;
        PendingQuestionCard.DismissRequested += OnQuestionDismissed;
        this.RefreshWhenAppResumes(RefreshUnattendedAsync);

        // Same reason and the same cadence as the dashboard: this screen is one CardiMember's
        // current state, and a caregiver watching it should not have to pull it down to find out
        // that it moved.
        this.RefreshEvery(PeriodicRefresh.LiveDataInterval, RefreshUnattendedAsync);

        TrendsCarousel.HeightRequest = MetricTrendCard.CardHeight;
        TrendsCarousel.PositionChanged += OnTrendPositionChanged;
        ContactsCarousel.PositionChanged += OnContactPositionChanged;
        TrendWindowPicker.WindowChanged += OnTrendWindowChanged;
    }

    public string MemberId
    {
        set
        {
            // Shell may set this after the page has already appeared and tried to load. Until
            // now the thirty-second tick below was what put that right, so a caregiver could sit
            // in front of the error card for most of a minute; the arrival itself reloads.
            var previous = _route.Id;
            var owed = _route.Accept(value);

            // A value the route could not use leaves the page alone. MemberRoute deliberately
            // keeps the id it already had rather than taking Guid.Empty, so clearing the cards
            // below would blank half a working screen for a member who is still on it — and
            // with no new id, nothing would come back to fill it in again.
            if (_route.Id == previous)
                return;

            // Whatever summary is on screen belongs to whoever was on screen before. It must not
            // be the reason the next CardiMember's placeholder is skipped, and the rung drawn
            // from it must not be read as the next CardiMember's.
            _digestRendered = false;
            _digest = null;
            UrgencyRow.IsVisible = false;
            // Hidden with the rest, and load-bearing now that a pass can skip the round trip:
            // LoadAdviseAsync reads the saved suggestion only when this card is down, so a card
            // left up from the CardiMember before would have stood, unrefreshed, under the new
            // one's name for as long as the cadence held.
            AdviseCard.IsVisible = false;
            PendingQuestionCard.IsVisible = false;
            QuestionsRow.IsVisible = false;

            // Cleared with them, and for the same reason. Every test above is "is this card up
            // yet", and the gate holds the other half of that — what has been drawn onto it. A
            // caregiver who steps from this CardiMember to another and back lands inside the
            // cadence window, so the pass makes no round trip and the saved copy is all there is;
            // left standing, a draw from the presentation they have already left would refuse it
            // and leave the card on its placeholder until the window ran out.
            _followUps.Cleared();

            if (owed)
                this.WhenRouteHasLanded(() => _ = LoadAsync(LoadTrigger.Arrival));
        }
    }

    /// <summary>
    /// Which section this page was opened for, when it was opened for one — see
    /// <see cref="AdviseFocus"/>. Left unset by every other way in, which is the ordinary
    /// top-of-page arrival.
    /// </summary>
    public string FocusSection
    {
        set => _focusAdvise = string.Equals(
            Uri.UnescapeDataString(value ?? string.Empty), AdviseFocus, StringComparison.Ordinal);
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
        _ = LoadAsync(LoadTrigger.Arrival);
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
        await LoadAsync(LoadTrigger.Requested);
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync(LoadTrigger.Requested);

    /// <summary>
    /// The quiet reload behind both unattended paths — the app returning to the foreground, and
    /// the timer ticking while the caregiver watches — for the same reason OnAppearing refetches:
    /// this screen shows one CardiMember's current state, and it should be current. Silent: an
    /// unrequested refresh that fails leaves what is on screen alone.
    /// </summary>
    private Task RefreshUnattendedAsync() =>
        DateTime.UtcNow - _lastLoadedUtc < ResumeRefresh.MinimumGap
            ? Task.CompletedTask
            : LoadAsync(LoadTrigger.Unattended);

    private async Task LoadAsync(LoadTrigger trigger)
    {
        var unattended = trigger == LoadTrigger.Unattended;
        var requested = trigger == LoadTrigger.Requested;

        if (_gate.IsLoading)
            return;

        // A navigation that couldn't carry a member id must not turn into traffic: with the
        // refresh timer below, an empty id became a request for member 00000000-… every thirty
        // seconds for as long as the page was on screen (seen live from dev, 2026-08-20). The
        // 404 the API would return lands on the same error card — just without the round trips.
        if (_route.IsMissing)
        {
            _route.LoadedWithoutId();
            ErrorDetailLabel.Text = MemberRoute.MissingMessage;
            SetState(error: true);
            return;
        }

        var ticket = _gate.Begin();
        var memberId = _route.Id;

        if (_member is null)
            SetState(loading: true);

        // Taken when the caregiver left if they left, and only otherwise from where the page
        // sits now. Captured once, ahead of both renders, so the saved snapshot and the live
        // answer put the caregiver back in the same place.
        var anchor = _anchorOnLeaving ?? CaptureScrollAnchor();
        _anchorOnLeaving = null;

        // Only this pass honours it. Every restore below re-asserts the same target, so the
        // suggestion holds its place while the digest above it rewrites itself; by the pass
        // after, the caregiver is sitting on that card and the ordinary anchor keeps them
        // there without any help.
        var focusAdvise = _focusAdvise;
        _focusAdvise = false;

        // Completed true once this pass has put this member on the screen, and false when it
        // never does. The three loads below start their round trips at once but wait on this
        // before they paint, because all three read state that belongs to the rendered member
        // rather than to the id they were given: the digest's urgency rung is computed against
        // _member, the question card is addressed with _member's first name, and the Advise card
        // marks its generation seen the moment it is drawn. Until the render, _member is still
        // whoever the page was showing before — on a reused page, a different CardiMember.
        //
        // Continuations run asynchronously so that none of that painting happens inside
        // SnapshotRefresh's render callback.
        var memberOnScreen = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Taken inside the try below and given back in its finally, which is why they are
        // declared out here. A pass owns the cards it claimed until it says otherwise, and every
        // way out of the load — superseded, a 404 over nothing, a fault while drawing — is a pass
        // that will never record them.
        var digestClaim = ReadClaim.None;
        var adviseClaim = ReadClaim.None;
        var questionsClaim = ReadClaim.None;
        var recorded = false;

        // The follow-up loads themselves, kept rather than dropped, because a claim is given back
        // when its read is over and these outlive this method — see the finally.
        var digestLoad = Task.CompletedTask;
        var adviseLoad = Task.CompletedTask;
        var questionsLoad = Task.CompletedTask;

        try
        {
            // Started before the member is awaited, not after it. These three only ever needed
            // the member id, which is known here, and queueing their round trips behind the
            // member's own is what left the summary card on its placeholder copy for two trips
            // instead of one.
            //
            // Nothing is recorded here. The schedule wants reads that were made for a member the
            // page ends up showing, and this pass may not leave one there at all — recording on
            // intent left the summary on its placeholder for a whole cadence window while the
            // tick that finally fetched the member declined to fetch the summary beside it. The
            // reads are recorded below, once the member load has settled.
            //
            // What is taken here is the claim, which is the other half of that: between this line
            // and the recording below, the schedule has no timestamp to show, and this page is
            // transient — a caregiver who leaves and comes back inside that second gets a second
            // page, which would find all three cards due and read them again beside these. The
            // claim says the read is already out; the claim's session is checked when it is handed
            // back, because a load is not cancelled by sign-out and this pass can land after the
            // next caregiver has signed in.
            digestClaim = _schedule.ClaimIfDue(memberId, GeneratedCard.Digest, requested);
            adviseClaim = _schedule.ClaimIfDue(memberId, GeneratedCard.Advise, requested);

            // The question has its own reason not to run: an editor someone is typing in makes
            // this pass skip it and nothing else. A skipped read is not recorded either, or
            // closing the editor would leave a question another caregiver has already answered
            // sitting there for the rest of the window.
            // The flag is consumed whether or not the read happens, so an editor open on the way
            // back does not leave it standing for a later pass to act on.
            var changedElsewhere = _questionsChangedElsewhere;
            _questionsChangedElsewhere = false;

            questionsClaim = PendingQuestionCard.IsEditing
                ? ReadClaim.None
                : _schedule.ClaimIfDue(memberId, GeneratedCard.Questions, requested || changedElsewhere);

            // Fire-and-forget, not awaited: each is a separate round trip that shouldn't hold
            // up the rest of the screen or the pull-to-refresh spinner.
            // Each of these lands above or around where the caregiver is reading and changes
            // the height of it — the digest rewrites the summary, the questionnaires add or
            // remove a whole card — so the anchor is re-asserted as each one finishes rather
            // than only after Apply. Restoring is a no-op when nothing moved.
            // All three run on every pass; what the cadence decides is whether each makes its
            // round trip. Each still reads the device's saved copy and puts it up, which is what
            // keeps a skipped read invisible: the page is rebuilt on every arrival, so a load
            // that simply did not run would leave the summary on its placeholder and the
            // suggestion card hidden — a far worse trade than the request it saved.
            //
            // They are also the only loads here with no gate over them, so they carry their own
            // order. Two passes can have reads out for the same card at once — this pass releases
            // the gate when the *member* lands, with these three still out — and without a pass
            // number the older answer could arrive last and draw over the newer one (#1105).
            var rendered = memberOnScreen.Task;
            var pass = _followUps.Begin();
            digestLoad = LoadThenRestoreAsync(
                LoadDigestAsync(memberId, pass, rendered, digestClaim.IsDue), anchor, focusAdvise);
            adviseLoad = LoadThenRestoreAsync(
                LoadAdviseAsync(memberId, pass, rendered, adviseClaim.IsDue), anchor, focusAdvise);
            questionsLoad = LoadThenRestoreAsync(
                LoadQuestionnairesAsync(memberId, pass, rendered, questionsClaim.IsDue),
                anchor, focusAdvise);

            var outcome = await SnapshotRefresh.RunAsync(
                _api, _gate, ticket,
                peek: _member is null ? ct => _api.PeekCardiMemberAsync(memberId, ct) : null,
                fetch: ct => _api.GetCardiMemberAsync(memberId, ct),
                render: member =>
                {
                    _member = member;
                    ChatBot.MemberId = memberId;
                    ChatBot.MemberFirstName = member.DisplayFirstName();
                    Apply(member);
                    SetState(loaded: true);
                    _ = RestoreScrollAnchorAsync(anchor, focusAdvise);
                },
                _feedback);

            switch (outcome.Result)
            {
                case RefreshResult.Superseded:
                    return;
                case RefreshResult.NothingAndFailed:
                    // Nothing to show — or a 404 over a snapshot, which means the member is gone
                    // (or was never this caregiver's) and the saved page must not stand in.
                    _member = null;
                    ErrorDetailLabel.Text = outcome.Error!.Message;
                    SetState(error: true);
                    return;
            }

            // The gate resolves once, here, rather than on the first render. SnapshotRefresh can
            // render twice — the saved copy, then the live one — and a card painted between them
            // is addressed to the saved copy: a name edited on another device would stay wrong on
            // the question card, which Apply never rebinds.
            //
            // The test is what the page is actually showing, not whether a render happened. A
            // refresh that failed over a member already up still counts: that member is on the
            // screen, these cards belong to them, and the reads this pass made are worth
            // recording. Counting only renders meant a passing outage relaunched all three on
            // every thirty-second tick for as long as it lasted, which is the opposite of the
            // cadence's purpose. A page still showing the *previous* member — the reused-page
            // window before the new one lands — fails the test, which is what it is for.
            // Both halves are needed. The first says the page is showing this member; the second
            // says the page still wants to — a pass can finish after the caregiver has been sent
            // on to another CardiMember. The schedule is keyed by member, so a late pass can no
            // longer hold back somebody else's cards; what this still stops is recording a read
            // as having reached a screen that had already moved on from it.
            var showingThisMember = _member?.Id == memberId && memberId == _route.Id;
            memberOnScreen.TrySetResult(showingThisMember);
            if (showingThisMember)
            {
                // Handed back card by card, including the ones this pass never claimed: Record
                // knows an empty claim when it sees one, and sorting them here would only be this
                // rule written down twice.
                //
                // Given back here and not when the reads land, unlike the abandoned case below,
                // because the stamp Record writes is what holds another screen off from this
                // moment on. The claim has nothing left to do.
                _schedule.Record(memberId, GeneratedCard.Digest, digestClaim);
                _schedule.Record(memberId, GeneratedCard.Advise, adviseClaim);
                _schedule.Record(memberId, GeneratedCard.Questions, questionsClaim);
                recorded = true;
            }

            if (outcome.IsFresh)
                _lastLoadedUtc = DateTime.UtcNow;
            else if (!unattended && outcome.Error is not null)
            {
                // Something is already on screen and the banner says it is saved; the caregiver
                // asked for this refresh, so the reason it did not happen is said as well.
                await _popups.ShowWarningAsync(outcome.Error.Message, "Couldn't refresh");
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
            // Every path that got here without rendering: superseded, a 404 over nothing, a
            // failed refresh that left the previous member's page up, or a fault inside Apply.
            // A no-op once the render has already set it.
            memberOnScreen.TrySetResult(false);

            // Every one of those paths is a pass that read for a member it never put on screen,
            // so its cards go back unread rather than being written down — and go back rather than
            // waiting out the lease, so the pass that does show this member is not made to wait on
            // this one's failure.
            //
            // But not yet: a card is being read until its read lands, and these three routinely
            // outlive the member load that got here. Handed back on this line, a second page would
            // find nothing claimed and start the same reads beside the ones still running, which
            // is the case the claim exists to stop. Each goes back when its own read is over, and
            // a read that never comes back at all is covered by the lease.
            if (!recorded)
            {
                _ = AbandonWhenDoneAsync(digestLoad, memberId, GeneratedCard.Digest, digestClaim);
                _ = AbandonWhenDoneAsync(adviseLoad, memberId, GeneratedCard.Advise, adviseClaim);
                _ = AbandonWhenDoneAsync(
                    questionsLoad, memberId, GeneratedCard.Questions, questionsClaim);
            }

            _gate.Release(ticket);
        }
    }

    /// <summary>
    /// Gives a claim back once the read it covers has finished, however it finished.
    /// </summary>
    /// <remarks>
    /// <see cref="LoadThenRestoreAsync"/> has already dealt with the load's failure, so this
    /// waits on it rather than guarding it; the abandon is in a finally all the same, because a
    /// claim nobody gives back is a card nobody may read until the lease runs out.
    /// </remarks>
    private async Task AbandonWhenDoneAsync(
        Task load, Guid memberId, GeneratedCard card, ReadClaim claim)
    {
        try
        {
            await load;
        }
        finally
        {
            _schedule.Abandon(memberId, card, claim);
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
    private async Task LoadThenRestoreAsync(Task load, ScrollAnchor? anchor, bool focusAdvise = false)
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
            await RestoreScrollAnchorAsync(anchor, focusAdvise);
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
    private async Task RestoreScrollAnchorAsync(ScrollAnchor? anchor, bool focusAdvise = false)
    {
        // An arrival aimed at the suggestion overrides the anchor rather than competing with it —
        // and on that arrival there is no anchor to override anyway, since the page opens at the
        // top and CaptureScrollAnchor returns null there.
        if (focusAdvise)
        {
            await FocusAdviseAsync();
            return;
        }

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

    /// <summary>
    /// Puts the "Something to try" (Advise) card under the top of the viewport, for an arrival that asked
    /// for it — see <see cref="AdviseFocus"/>.
    /// </summary>
    /// <remarks>
    /// A no-op while the card is still hidden, which it is until <see cref="LoadAdviseAsync"/>
    /// lands: this runs after every section of the arriving pass, so the one that follows the
    /// suggestion itself is the one that moves the page, and the ones after that hold it there
    /// as the digest above rewrites itself. Unanimated for the same reason the anchor restore is
    /// — a caregiver who tapped Advise should find themselves at the suggestion, not watch the
    /// page travel to it.
    /// </remarks>
    private async Task FocusAdviseAsync()
    {
        await Task.Yield();

        if (!AdviseCard.IsVisible)
            return;

        try
        {
            await DetailScroller.ScrollToAsync(AdviseCard, ScrollToPosition.Start, animated: false);
        }
        catch (Exception)
        {
            // The page went away mid-refresh. Nothing left to scroll.
        }
    }

    /// <summary>
    /// Shows "Who can see them" only to the admin of the family that owns this member — the one
    /// caller the invitation endpoints answer. Best-effort and after the render: the row is an
    /// extra, and a families call that fails must not cost the page that is already up.
    /// </summary>
    private async Task ApplyWhoCanSeeAsync(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            WhoCanSeeRow.IsVisible = false;
            return;
        }

        try
        {
            var families = await _api.GetMyFamiliesAsync();
            var family = families.FirstOrDefault(f => f.OrganizationId == organizationId);
            WhoCanSeeRow.IsVisible = family is not null && FamilyTabState.IsAdmin(family.Role);
        }
        catch (ApiException)
        {
            WhoCanSeeRow.IsVisible = false;
        }
    }

    private void Apply(CardiMemberDetailResponse member)
    {
        _ = ApplyWhoCanSeeAsync(member.OrganizationId);
        Avatar.Apply(member.Name, member.PhotoUrl);
        NameLabel.Text = member.DisplayFirstName();
        AgeRelationshipLabel.Text = $"{member.Age} years old • {member.Relationship.GetDisplayName()}";

        ApplyInsight(member.Insight);

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
        ApplyManagementLines(member);
        // Only on the paused branch: Apply also runs on the periodic refresh, and closing a drop
        // down the caregiver is reading mid-refresh would be the refresh taking the choice away.
        if (member.MonitoringPaused)
            ResetPauseDurations();

        // How closely this member is watched (M1-14). Recorded preference only for now — see
        // AlertSensitivity — so it says what the family asked for, not what the pipeline does.
        // The pill's weight climbs with the level, so the setting reads before the word does.
        SensitivityLabel.Text = $"{member.AlertSensitivity.GetDisplayName()} alert sensitivity";
        var sensitivityAccent =
            (Color)Microsoft.Maui.Controls.Application.Current!.Resources["Primary"];
        (SensitivityPill.BackgroundColor, SensitivityLabel.TextColor) = member.AlertSensitivity switch
        {
            AlertSensitivity.High => (sensitivityAccent, (Color)Microsoft.Maui.Controls.Application.Current!.Resources["White"]),
            AlertSensitivity.Low => (sensitivityAccent.WithAlpha(0.10f),
                (Color)Microsoft.Maui.Controls.Application.Current!.Resources["BodyText"]),
            _ => (sensitivityAccent.WithAlpha(0.18f),
                (Color)Microsoft.Maui.Controls.Application.Current!.Resources["PrimaryDark"]),
        };

        // Same four-tier pipeline freshness as the dashboard (red / amber / blue / green). Hidden
        // while paused: collection is deliberately stopped, so a coloured dot would misreport a
        // pause as a connection gap. The paused banner above is the status in that case.
        SyncStatusTap.IsVisible = !member.MonitoringPaused;
        var freshnessColor = FreshnessPalette.ColorFor(member.DataFreshness);
        ConnectionStatusDot.Fill = freshnessColor;

        // The dot is the whole of it on the card now, so it carries the state a screen reader
        // would otherwise have read off the line that used to sit beside it, and a tap spells
        // the same thing out on screen (OnSyncStatusTapped).
        SemanticProperties.SetDescription(
            SyncStatusTap, $"{member.DataFreshnessMessage}. {LastSyncedSummary(member)}");

        // The digest has its own round trip (LoadDigestAsync). That trip now runs alongside this
        // one rather than behind it, but it still paints after this method has returned — it
        // waits on the render — so writing the placeholder every time meant every refresh,
        // including the unattended periodic one, shrank this card back to two lines and then grew
        // it again a moment later. That is two layout passes for a summary that has usually not
        // changed at all, and it shoves Key Metric Trends and everything under it down the page
        // and back twice while the caregiver is reading them. The placeholder is for a screen that has nothing better
        // on it; once a summary is up it stays up until there is a new one, which is the same
        // stance the failed-refresh path above takes.
        if (!_digestRendered)
        {
            SummaryTitleLabel.Text = "Still getting to know them";
            SummaryGeneratedLabel.IsVisible = false;
            SummaryLabel.Text = $"We'll summarise how {member.DisplayFirstName()} is doing here as soon as there's enough data to say something useful.";
        }

        // Both read the member as well as the digest, so they belong to every pass that lands new
        // member data — not only to the digest's own round trip. HealthStatus is what the note
        // speaks about, and it is this call that just refreshed it: recomputing here is what
        // retracts the note in the same pass as the alert it named stops being open.
        ApplyUrgency(_digest?.Urgency);

        ApplyTrends(member.Metrics);
        ApplyContacts(member);

        // Standing way in: a caregiver can volunteer a fact before the service has asked
        // anything. Independent of LoadQuestionnairesAsync, which a cadence skip can return
        // from without painting when there is nothing cached.
        QuestionsRow.IsVisible = true;

        // Only a primary caregiver may edit, pause or remove — the API enforces this and
        // would answer 404, so showing the controls would just be a trap.
        EditButton.IsVisible = member.IsPrimaryCaregiver;
    }

    /// <summary>
    /// Whether a follow-up load may put its live answer on screen: the page is still on the member
    /// it was started for, and nothing newer has already drawn this card.
    /// </summary>
    /// <remarks>
    /// The member is checked first and the pass second, because asking the gate is what records
    /// the draw — a load that is not going to draw anything must not be written down as the card's
    /// newest answer. For the same reason every caller keeps this last in its condition.
    /// The route is checked as well as the pass, and not only because it is cheaper: a member id
    /// arriving from Shell moves the page before any new pass has begun, so for that moment the
    /// newest pass is one belonging to the CardiMember who has just been left.
    /// </remarks>
    private bool MayDrawLive(Guid memberId, FollowUpPass pass, GeneratedCard card) =>
        memberId == _route.Id && _followUps.MayDrawLive(pass, memberId, card);

    /// <summary>
    /// Whether the device's saved copy of a card may still go up — see
    /// <see cref="FollowUpGate.MayDrawSaved"/>. The card's own "is it up yet" test is what decides
    /// whether to read the saved copy at all; this is the same question asked again on the way
    /// back, because a live answer can have arrived while the peek was out.
    /// </summary>
    private bool MayDrawSaved(Guid memberId, GeneratedCard card) =>
        memberId == _route.Id && _followUps.MayDrawSaved(memberId, card);

    /// <summary>
    /// Best-effort, like the dashboard's live status line: no spinner, no error state. The
    /// placeholder <see cref="Apply"/> renders is a complete fallback on its own, so a 404
    /// (nothing generated yet) or a failed call just leaves the card to it. The read runs
    /// alongside the member's own rather than behind it; the drawing still waits for that render.
    /// </summary>
    private async Task LoadDigestAsync(
        Guid memberId, FollowUpPass pass, Task<bool> memberOnScreen, bool fetchLive)
    {
        // Started before anything is awaited — this round trip running alongside the member's own
        // is the whole point of the early start. Only the painting below waits.
        var fetch = fetchLive ? _api.GetDigestAsync(memberId) : null;

        try
        {
            if (!await memberOnScreen)
            {
                // The pass never put this member on the page, so there is nothing to write this
                // onto. The answer is moot; awaiting it anyway is what keeps a failure from
                // becoming a task nobody observed.
                if (fetch is not null)
                    await fetch;
                return;
            }

            // The device's saved summary first, when nothing is up yet, so the card reads as
            // written rather than as a placeholder for a round trip that is still out; the live
            // one lands on top and only re-fades when the words actually moved. No overlay for
            // these follow-up loads — the page's own replacement already had one.
            //
            // Asked twice, because the peek is itself a wait: a live answer — this pass's or
            // another pass's — can arrive while it is out, and the saved copy going up afterwards
            // would put the cache back over fresher words.
            var saved = _digestRendered ? null : await _api.PeekDigestAsync(memberId);
            if (saved is not null && MayDrawSaved(memberId, GeneratedCard.Digest))
                ApplyDigest(saved);

            // The peek is why a skipped read is invisible: the page is rebuilt on every arrival,
            // so without it a caregiver stepping back in would meet the "Still getting to know
            // them" placeholder instead of the summary they just read.
            //
            // And it is why a skipped read needs no fallback to the network when the device has
            // nothing saved. That happens for two reasons and neither wants a request here. The
            // member may have no summary yet, in which case the placeholder is the right answer
            // and fetching on every tick to be told so again is the cost this whole change
            // exists to remove. Or another pass's read is still in flight — which is now a thing
            // the schedule says outright, holding this pass off the wire rather than letting it
            // ask for what is already on its way — in which case that answer lands in the cache
            // within moments and the peek above, which runs on every pass while the card is
            // empty, picks it up on the next tick. That wait is the price of the claim, and it is
            // the same wait the cadence already asks a skipped read to take.
            if (fetch is null)
                return;

            // Only what is drawn is decided here. The answer has already been written to the
            // device's cache by the time it gets back — the client saves every successful GET —
            // so a superseded one is kept from putting older words back there by the client's own
            // ordering, not by this check, which cannot reach that far.
            var digest = await fetch;
            if (!MayDrawLive(memberId, pass, GeneratedCard.Digest))
                return;

            ApplyDigest(digest);
        }
        catch (ApiException)
        {
            // Placeholder copy stays — see the field's own comment in Apply().
        }
    }

    private void ApplyDigest(DigestResponse digest)
    {
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

        _digest = digest;
        if (_member is not null)
            ApplyUrgency(digest?.Urgency);

        if (unchanged)
            return;

        // Reads as an update rather than a flicker, and only when the words actually moved —
        // same treatment as the dashboard's status hero.
        SummaryTitleLabel.Opacity = 0;
        SummaryLabel.Opacity = 0;
        _ = SummaryTitleLabel.FadeToAsync(1, 150, Easing.CubicOut);
        _ = SummaryLabel.FadeToAsync(1, 150, Easing.CubicOut);
    }

    /// <summary>
    /// Best-effort, same treatment as <see cref="LoadDigestAsync"/>: the Advise card starts hidden,
    /// which is a complete fallback on its own, so a failed call just leaves it that way. Unlike
    /// the digest endpoint, a 404 here isn't "nothing generated yet" — that case is a 200 with a
    /// blank <see cref="AdviseResponse.Suggestion"/> (<see cref="ApplyAdvise"/> hides the card for
    /// it) — a 404 means access was refused or the member doesn't exist.
    /// </summary>
    private async Task LoadAdviseAsync(
        Guid memberId, FollowUpPass pass, Task<bool> memberOnScreen, bool fetchLive)
    {
        var fetch = fetchLive ? _api.GetAdviseAsync(memberId) : null;

        try
        {
            // Waited on rather than raced, and this card is the sharpest reason why: ApplyAdvise
            // marks the generation seen as it draws, so painting it over a page that is still a
            // skeleton — or still the previous CardiMember — would take the Advise glyph off the
            // dashboard for a suggestion nobody has been shown.
            if (!await memberOnScreen)
            {
                if (fetch is not null)
                    await fetch;
                return;
            }

            // Saved suggestion first when the card is not up yet; the live one lands on top. On a
            // pass the cadence has skipped, the saved one is all there is — and all there needs
            // to be, the page having been rebuilt around it since it was written.
            var saved = AdviseCard.IsVisible ? null : await _api.PeekAdviseAsync(memberId);
            if (saved is not null && MayDrawSaved(memberId, GeneratedCard.Advise))
                ApplyAdvise(saved);

            // No fallback to the network when the peek finds nothing — see LoadDigestAsync. It
            // bites hardest here: a member with no suggestion yet has nothing to cache, so a
            // fallback would put this endpoint back on the thirty-second tick for exactly the
            // members it has least to say about.
            if (fetch is null)
                return;

            var advise = await fetch;
            if (!MayDrawLive(memberId, pass, GeneratedCard.Advise))
                return;

            ApplyAdvise(advise);
        }
        catch (ApiException)
        {
            // No suggestion yet — the card stays hidden, same as Apply()'s placeholder stance.
        }
    }

    /// <summary>
    /// How this member reads against their own normal, and the longer view where there is one.
    /// </summary>
    /// <remarks>
    /// Every label hides on its own rather than the card rendering an empty line: a member with a
    /// baseline reading and no trend yet is the normal case for the first month, and a heading
    /// over nothing reads as something that failed to load. The whole card hides when the server
    /// sent no block at all — which it does while monitoring is paused, because a reading of how
    /// someone is doing describes watching that has stopped.
    /// </remarks>
    private void ApplyInsight(MemberInsightResponse? insight)
    {
        // Two surfaces rather than one card with two halves: the working sits folded into the
        // summary above, and the longer view keeps a card, so either can be absent without
        // leaving the other under a heading with nothing beneath it.
        var hasSummary = !string.IsNullOrWhiteSpace(insight?.Summary);
        var hasTrend = !string.IsNullOrWhiteSpace(insight?.Trend);

        // The section comes down entirely when there is nothing behind it. A header and a chevron
        // over an empty body is the worst of both: it takes a line of the card, and it invites a
        // caregiver to open it to find out there was nothing there.
        var movements = insight?.Movements ?? [];
        var hasAnythingToList = movements.Count > 0 || insight?.KeyFindings.Count > 0;

        InsightAccordion.IsVisible = hasSummary || hasAnythingToList;
        InsightCard.IsVisible = hasTrend;

        if (insight is null)
            return;

        InsightSummaryLabel.IsVisible = hasSummary;
        InsightSummaryLabel.Text = insight.Summary ?? string.Empty;

        // Cards where the row carries its movements, the older prose list where it does not.
        // Never both: the cards say what the bullets say, with the figures the bullets could not
        // carry, and a row written before the movements column existed still has to render.
        InsightMovementCards.Apply(movements);
        InsightFindings.Apply(movements.Count > 0 ? [] : insight.KeyFindings);

        // What is behind the chevron is only called a trend when it is one. A member still being
        // learned has findings — notes about a picture that is still forming — and naming those
        // "trends to keep an eye on", with a red count beside them, tells a caregiver something
        // has been judged about someone CardiTrack has not finished measuring.
        var hasTrends = movements.Count > 0
            || (!insight.IsLearning && insight.KeyFindings.Count > 0);

        InsightAccordion.HeaderText = InsightHeader(hasTrends);
        InsightAccordion.Count = hasTrends
            ? (movements.Count > 0 ? movements.Count : insight.KeyFindings.Count)
            : null;
        InsightAccordion.CountTint = MovementCards.DominantTint(movements);

        // The body was measured when it was empty, so an open accordion filled by a later load
        // would be sliced off at whatever it was worth then.
        InsightAccordion.RefreshHeight();

        InsightTrendHeader.IsVisible = hasTrend;
        InsightTrendLabel.IsVisible = hasTrend;
        InsightTrendLabel.Text = insight.Trend ?? string.Empty;

        // Only alongside a narrative: points under a heading with no account above them would be
        // the trend half claiming more than it has.
        InsightTrendFindings.Apply(hasTrend ? insight.TrendFindings : []);

        InsightGeneratedLabel.IsVisible = hasTrend && insight.GeneratedAt is not null;
        InsightGeneratedLabel.Text = InsightFooter(insight);
    }

    /// <summary>
    /// The accordion's header, saying how much is folded behind it.
    /// </summary>
    /// <remarks>
    /// The weakness of putting anything behind a chevron is that the chevron says nothing about
    /// whether opening it is worth doing. A caregiver checking on someone should not have to open
    /// a section to find out whether it has anything in it — and with the card now appearing only
    /// when something has actually moved, the count is the answer to the question they came with.
    /// </remarks>
    private static string InsightHeader(bool hasTrends) =>
        hasTrends
            ? "Trends to keep an eye on"
            // Nothing has been judged to keep an eye on, so the section is what it always was:
            // the learning and provisional readings, which describe a picture still forming.
            // Naming those "trends" would promise a judgement that has not been made.
            : "How they are doing";

    /// <summary>
    /// Opens the alarm form on the reading a trend card was raised from.
    /// </summary>
    /// <remarks>
    /// The metric and a starting threshold travel with it, so a caregiver who has just been shown
    /// that someone is sleeping less than usual does not land on an empty picker and have to find
    /// "Sleep duration" again. Both are pre-filled rather than saved: the form holds everything
    /// until Save, and the threshold is this member's own departure rather than a level the app
    /// has decided matters.
    /// </remarks>
    private async Task OpenAlertForMovementAsync(MemberMovementResponse movement)
    {
        if (movement.AlarmMetric is not { } metric)
            return;

        var route = $"{MetricAlarmEditPage.Route}?memberId={_route.Id}"
            + $"&name={Uri.EscapeDataString(_member?.DisplayFirstName() ?? string.Empty)}"
            + $"&metric={Uri.EscapeDataString(metric)}";

        if (movement.SuggestedThresholdPercent is { } threshold)
        {
            route += "&threshold="
                + Uri.EscapeDataString(threshold.ToString(CultureInfo.InvariantCulture));
        }

        await Shell.Current.GoToAsync(route);
    }

    /// <summary>
    /// The window the comparison is against, and when it was written. Both, because either alone
    /// leaves a caregiver to assume the other: a reading with no date invites them to treat last
    /// week's picture as this morning's, and one with no window does not say how much of their
    /// life it is measured against.
    /// </summary>
    private static string InsightFooter(MemberInsightResponse insight)
    {
        if (insight.GeneratedAt is not { } generated)
            return string.Empty;

        var written = RelativeTime.Format(generated);

        if (insight.IsLearning)
            return $"Still getting to know them · {written}";

        var window = insight.BaselinePeriodDays is { } days
            ? insight.IsProvisional
                ? $"Against an early {days}-day picture"
                : $"Against their last {days} days"
            : "Against their own usual";

        return $"{window} · {written}";
    }

    /// <summary>
    /// Shows the "Something to try" (Advise) card, or hides it when there is nothing to suggest right now
    /// — a blank <see cref="AdviseResponse.Suggestion"/> means exactly that, not a failed call.
    /// </summary>
    private void ApplyAdvise(AdviseResponse advise)
    {
        if (string.IsNullOrWhiteSpace(advise.Suggestion))
        {
            AdviseCard.IsVisible = false;
            return;
        }

        AdviseSummaryLabel.Text = advise.Summary;
        AdviseSuggestionLabel.Text = advise.Suggestion;
        // The safety framing is always there and always worded the same; the guideline, when
        // the model cited one, leads into it so the footnote reads as one sentence.
        AdviseGuidelineLabel.Text = string.IsNullOrWhiteSpace(advise.GuidelineCited)
            ? "Just a suggestion, never medical advice — worth mentioning to their doctor."
            : $"Based on {advise.GuidelineCited.TrimEnd('.')} — just a suggestion, never medical advice; worth mentioning to their doctor.";
        // Load-bearing next to a daily regeneration cadence: without it, yesterday's suggestion
        // beside today's hourly summary reads as the two disagreeing about today.
        AdviseGeneratedLabel.Text = $"Updated {RelativeTime.Format(advise.GeneratedAt.UtcDateTime)}";
        AdviseGeneratedLabel.IsVisible = true;
        // Rendering it is reading it: the dashboard card's Advise glyph stops being coloured for
        // this generation whether the caregiver came through that button or scrolled here.
        AttentionMarks.MarkSeen(AttentionMarks.Advise, _route.Id, advise.GeneratedAt.UtcDateTime);
        AdviseCard.IsVisible = true;
    }

    /// <summary>
    /// Loads the questions asked about this member: the one still waiting goes on the page, and the
    /// Q&amp;A row is the standing way in to volunteer a fact or read earlier answers.
    /// </summary>
    /// <remarks>
    /// Best-effort in the same way as the summary — a question is an extra, and a failed call
    /// leaves the page looking exactly as it does for a member with nothing to answer.
    /// </remarks>
    private async Task LoadQuestionnairesAsync(
        Guid memberId, FollowUpPass pass, Task<bool> memberOnScreen, bool fetchLive)
    {
        // A refresh must not rebuild the card under an editor someone is typing in — QuestionCard
        // .Apply closes it and replaces its text, so this is someone's half-written answer. Same
        // courtesy the pause drop down gets; the cost is one stale card until the next load.
        // LoadAsync checks this too, so that a pass it stops there is not recorded as a read.
        if (PendingQuestionCard.IsEditing)
            return;

        var fetch = fetchLive ? _api.GetQuestionnairesAsync(memberId) : null;

        try
        {
            // ApplyQuestionnaires addresses the card with _member's first name, so drawing it
            // before the render would ask after whoever the page was showing before — or after
            // nobody at all, on a first load.
            if (!await memberOnScreen)
            {
                if (fetch is not null)
                    await fetch;
                return;
            }

            // Re-checked against every apply below, not only on the way in. The guard at the top
            // of this method runs before two waits — the member render and the round trip — and
            // an editor opened during either of them holds text that Apply would throw away.
            if (PendingQuestionCard.IsEditing)
            {
                if (fetch is not null)
                    await fetch;
                return;
            }

            // The saved page first when no pending card is up yet — a question the device already
            // holds is on screen at once — and the live page on top of it. The Q&A row is a
            // standing way in (volunteer a fact before anything has been asked), not a signal that
            // questionnaire data has been applied, so it is not part of this check. Same shape as
            // Advise peeking while its card is down. The validity check below runs on both the
            // saved and live pages, which is what stops a saved question about a day that has
            // ended being asked again.
            var saved = PendingQuestionCard.IsVisible
                ? null
                : await _api.PeekQuestionnairesAsync(memberId);
            if (saved is not null
                && !PendingQuestionCard.IsEditing
                && MayDrawSaved(memberId, GeneratedCard.Questions))
                ApplyQuestionnaires(saved);

            // No fallback to the network when the peek finds nothing — see LoadDigestAsync. A
            // member with nothing to answer is the ordinary case here, and it is indistinguishable
            // from an empty cache without asking.
            if (fetch is null)
                return;

            var result = await fetch;
            if (PendingQuestionCard.IsEditing
                || !MayDrawLive(memberId, pass, GeneratedCard.Questions))
                return;

            ApplyQuestionnaires(result);
        }
        catch (ApiException)
        {
            // No card, no row, no error state: the page is complete without either.
        }
    }

    private void ApplyQuestionnaires(QuestionnairesPageResponse result)
    {
        QuestionsRow.IsVisible = true;

        // Checked before it is drawn, not trusted because the API sent it. A card held on
        // screen across midnight, or a page served from the offline cache after a night with no
        // signal, both hand us a "did they feel tired today?" about a day that has ended. The
        // service also tells the server, so the row stops blocking the next question.
        var pending = _questionValidity.Verify(result.Pending);
        if (pending is null)
        {
            PendingQuestionCard.IsVisible = false;
            return;
        }

        var alreadyShowing = PendingQuestionCard.IsVisible
                             && PendingQuestionCard.Questionnaire?.Id == pending.Id;

        PendingQuestionCard.Apply(pending, (_member?.DisplayFirstName() ?? string.Empty));
        PendingQuestionCard.IsVisible = true;

        if (alreadyShowing)
            return;

        // Reads as the question arriving rather than as a flicker — the summary's treatment.
        PendingQuestionCard.Opacity = 0;
        _ = PendingQuestionCard.FadeToAsync(1, 150, Easing.CubicOut);
    }

    private async void OnQuestionAnswered(object? sender, string answer)
    {
        if (PendingQuestionCard.Questionnaire is not { } questionnaire || _isBusy)
            return;

        // The editor may have been open a while. An answer to "how was their day today?" filed
        // after that day ended is filed against the wrong one, so the question goes rather than the
        // answer landing somewhere it does not belong.
        if (_questionValidity.Verify(questionnaire) is null)
        {
            PendingQuestionCard.CloseEditor();
            PendingQuestionCard.IsVisible = false;
            await _popups.ShowWarningAsync(
                "That one was about a day that's now over, so we've let it go. We'll ask again if "
                + "it still matters.",
                "This question has passed");
            return;
        }

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

            // Meant to reconcile the case where someone else answered it first, by finding
            // nothing pending and taking the card away. It does not currently get that far: the
            // editor is deliberately left open here so the text can be retried, and this reload
            // stops at the editing guard — see #1106. Left in place rather than removed, because
            // the reconciliation is the wanted behaviour and the open question is how to take a
            // card away from under someone's half-written answer, not whether to try.
            _ = LoadQuestionnairesAsync(
                _route.Id, _followUps.Begin(), AlreadyOnScreen, fetchLive: true);

            // Nothing is recorded for it, because nothing is read. The editor is deliberately
            // still open here so the text can be retried, and the reload stops at the editing
            // guard before it asks for anything (#1106). Recording a read that did not happen
            // would hold the next real one back for the whole window — after a failed save is
            // exactly when a question answered by somebody else needs to be noticed. Whatever
            // makes this path fetch should record it at the point it does the fetching.
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

    /// <summary>
    /// Rebuilds the trends carousel, one card per metric this member actually reports. The
    /// caregiver's chosen window survives a refresh, and so does the card they were looking at —
    /// pulling to refresh should not shuffle the screen back to the first metric under them.
    /// </summary>
    private void ApplyTrends(DashboardMetrics? metrics)
    {
        var position = TrendsCarousel.Position;
        var firstName = (_member?.DisplayFirstName() ?? string.Empty);

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
        foreach (var (icon, ink, name, value, axis, period, select) in reported)
        {
            _trends.Add(new MetricTrend(
                icon, ink, name, value, axis, period, select(metrics!),
                TrendWindowPicker.SelectedDays, firstName)
            {
                MemberId = _route.Id,
            });
        }

        // Assigning the same list instance back would not re-run the carousel's own diffing, so
        // hand it a fresh snapshot; the cards themselves are recycled either way.
        TrendsCarousel.ItemsSource = _trends.ToList();
        TrendsSection.IsVisible = _trends.Count > 0;
        if (_trends.Count == 0)
        {
            BuildIndicators(TrendIndicatorPanel, _trendIndicators, 0);
            return;
        }

        BuildIndicators(TrendIndicatorPanel, _trendIndicators, _trends.Count);
        TrendsCarousel.Position = Math.Clamp(position, 0, _trends.Count - 1);
        // Read back rather than trusting the write: a carousel that has not been laid out yet keeps
        // the position it had, and the dots must say whatever the carousel actually settled on.
        PaintIndicators(_trendIndicators, TrendsCarousel.Position);
    }

    /// <summary>
    /// Emergency contact and the member's own phone as two looping slides. Always both: an
    /// empty card is the graceful-absence copy, not a reason to drop the slide.
    /// </summary>
    private void ApplyContacts(CardiMemberDetailResponse member)
    {
        var hasEmergencyContact = !string.IsNullOrWhiteSpace(member.EmergencyContactName)
            || !string.IsNullOrWhiteSpace(member.EmergencyContactPhone);
        var hasPhone = !string.IsNullOrWhiteSpace(member.Phone);

        var emergency = _contacts[0];
        emergency.Primary = hasEmergencyContact
            ? member.EmergencyContactName ?? "Not named"
            : "No emergency contact yet";
        emergency.Secondary = hasEmergencyContact
            ? member.EmergencyContactPhone ?? "No number"
            : "Add one so help is one tap away";
        emergency.ShowCall = !string.IsNullOrWhiteSpace(member.EmergencyContactPhone);
        emergency.ShowEdit = member.IsPrimaryCaregiver;
        emergency.EditDescription = "Edit emergency contact";

        var phone = _contacts[1];
        phone.Primary = hasPhone ? member.Phone! : "No phone number yet";
        phone.ShowCall = hasPhone;
        phone.ShowMessage = hasPhone;
        // Whether the record is empty or wrong, the way in is the same one — a pencil that appears
        // only on an empty card can add a number and never correct one.
        phone.ShowEdit = member.IsPrimaryCaregiver;
        phone.EditDescription = "Edit phone number";

        // Bind once: a new ItemsSource re-realises the slides and snaps the carousel back to
        // the first card, which is the same jolt ApplyTrends already refuses to cause.
        if (_contactsBound)
            return;

        ContactsCarousel.ItemsSource = _contacts;
        BuildIndicators(ContactIndicatorPanel, _contactIndicators, _contacts.Count);
        PaintIndicators(_contactIndicators, ContactsCarousel.Position);
        _contactsBound = true;
    }

    private void OnTrendWindowChanged(object? sender, int days)
    {
        // The cards redraw themselves off this — see MetricTrend's own remarks on why the window
        // lives on the item rather than being pushed into each realised card.
        foreach (var trend in _trends)
            trend.Days = days;
    }

    private void OnTrendPositionChanged(object? sender, PositionChangedEventArgs e) =>
        PaintIndicators(_trendIndicators, e.CurrentPosition);

    private void OnContactPositionChanged(object? sender, PositionChangedEventArgs e) =>
        PaintIndicators(_contactIndicators, e.CurrentPosition);

    private static void BuildIndicators(HorizontalStackLayout panel, List<BoxView> dots, int count)
    {
        panel.Clear();
        dots.Clear();
        // A single slide is not a carousel; dots under it would promise a swipe that goes nowhere.
        panel.IsVisible = count > 1;
        if (count <= 1)
            return;

        for (var i = 0; i < count; i++)
        {
            var dot = new BoxView { WidthRequest = 8, HeightRequest = 8, CornerRadius = 4 };
            dots.Add(dot);
            panel.Add(dot);
        }
    }

    private static void PaintIndicators(IReadOnlyList<BoxView> dots, int position)
    {
        var count = dots.Count;
        if (count == 0)
            return;

        // Loop wraps the carousel; the reported position still sits in 0..count-1, but a wrap
        // that overshoots is folded back so the pill cannot light a slot that is not there.
        var active = ((position % count) + count) % count;
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        for (var i = 0; i < count; i++)
        {
            dots[i].WidthRequest = i == active ? 24 : 8;
            dots[i].Color = (Color)resources[i == active ? "ActiveIndicator" : "InactiveIndicator"];
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
        await Shell.Current.GoToAsync($"{MedicalInformationPage.Route}?memberId={_route.Id}");

    /// <summary>
    /// Which alerts CardiTrack checks for. The name rides along so the page's subtitle is right
    /// from the first frame, and whether this caregiver may change a rule rides with it so the
    /// page need not fetch the member again to find out.
    /// </summary>
    private async void OnAlertSettingsTapped(object? sender, TappedEventArgs e)
    {
        var name = Uri.EscapeDataString(_member?.DisplayFirstName() ?? string.Empty);
        var canManage = _member?.IsPrimaryCaregiver == true;
        await Shell.Current.GoToAsync(
            $"{AlertSettingsPage.Route}?memberId={_route.Id}&name={name}&canManage={canManage}");
    }

    private async void OnMetricAlarmsTapped(object? sender, TappedEventArgs e)
    {
        var name = Uri.EscapeDataString(_member?.DisplayFirstName() ?? string.Empty);
        var canManage = _member?.IsPrimaryCaregiver == true;
        await Shell.Current.GoToAsync(
            $"{MetricAlarmsPage.Route}?memberId={_route.Id}&name={name}&canManage={canManage}");
    }

    private async void OnContactCallTapped(object? sender, TappedEventArgs e)
    {
        var phone = PhoneFor(ItemOf(sender));
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
    private async void OnContactMessageTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf(sender) is not { Kind: ContactCardItem.Phone })
            return;

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

    /// <summary>
    /// Opens the slide's own record in a form of just that record's fields, and saves what comes
    /// back. Both slides now, and both in place: this used to be a trip to M1-14 with the phone
    /// field focused — the whole profile form, every field of it disturbable, to fix one number,
    /// and the emergency contact had no way in from its card at all.
    /// </summary>
    private async void OnContactEditTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf(sender) is not { ShowEdit: true } item || _member is null || _isBusy)
            return;

        var editingEmergency = item.Kind == ContactCardItem.Emergency;
        var kind = editingEmergency ? ContactEditKind.EmergencyContact : ContactEditKind.MemberPhone;

        // Normalised on the way in so that what comes back can be compared with it: the form
        // trims and blanks-to-null what it returns, and a stored " " would otherwise read as a
        // change every time the caregiver opened the form and closed it again.
        var name = editingEmergency ? NullIfEmpty(_member.EmergencyContactName) : null;
        var phone = NullIfEmpty(editingEmergency ? _member.EmergencyContactPhone : _member.Phone);

        var edit = await _popups.EditContactAsync(kind, name, phone);

        // Cancelled, or saved without having changed anything. Either way there is nothing to
        // send, and a PUT that rewrites a record to what it already said is still a write.
        if (edit is null || (edit.Name == name && edit.Phone == phone))
            return;

        await SaveContactAsync(editingEmergency, edit);
    }

    /// <summary>
    /// Sends one contact record's edit as the full-replacement update the API takes.
    /// </summary>
    /// <remarks>
    /// Everything the form did not ask about is echoed back from the copy on screen, because
    /// <see cref="UpdateCardiMemberRequest"/> is a replacement rather than a patch and an omitted
    /// field is a cleared one. The two exceptions are the two fields that do mean "leave it
    /// alone" when omitted — sex and the photo — and they are omitted for exactly that reason: a
    /// form that never showed them must not be the thing that restates them.
    /// </remarks>
    private async Task SaveContactAsync(bool editingEmergency, ContactEdit edit)
    {
        if (_member is null)
            return;

        _isBusy = true;
        try
        {
            var request = new UpdateCardiMemberRequest
            {
                // Display* rather than the raw fields: from an API that predates the split, FirstName
                // is empty and the parts come from the full name. Name is the old API's single name,
                // held to its 2–100 rule; a current API ignores it whenever FirstName is sent.
                FirstName = _member.DisplayFirstName(),
                LastName = _member.DisplayLastName(),
                Name = MemberNameRules.LegacyName(_member.DisplayFirstName(), _member.DisplayLastName()),
                DateOfBirth = _member.DateOfBirth,
                RelationshipType = _member.Relationship,
                Email = _member.Email,
                Phone = editingEmergency ? _member.Phone : edit.Phone,
                EmergencyContactName = editingEmergency ? edit.Name : _member.EmergencyContactName,
                EmergencyContactPhone = editingEmergency ? edit.Phone : _member.EmergencyContactPhone,
                // A contact edit: the notes are left alone, not echoed back as an edit — see
                // EditCardiMemberPage.
                MedicalNotes = _member.MedicalNotes,
                LeaveMedicalNotes = true,
                AlertSensitivity = _member.AlertSensitivity,
            };

            // The response is the saved member, so the cards are repainted from what the server
            // stored rather than from what was typed. ApplyContacts alone rather than the whole
            // of Apply: nothing else on this screen reads these two fields, and re-running Apply
            // would move sections the caregiver is not looking at.
            _member = await _api.UpdateCardiMemberAsync(_route.Id, request);
            ApplyContacts(_member);
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(
                ex.Errors is { Count: > 0 } ? string.Join('\n', ex.Errors) : ex.Message,
                editingEmergency ? "Couldn't save this contact" : "Couldn't save this number");
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

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ContactCardItem? ItemOf(object? sender)
    {
        for (var current = sender as Element; current is not null; current = current.Parent)
        {
            if (current.BindingContext is ContactCardItem item)
                return item;
        }

        return null;
    }

    private string? PhoneFor(ContactCardItem? item) => item?.Kind switch
    {
        ContactCardItem.Emergency => _member?.EmergencyContactPhone,
        ContactCardItem.Phone => _member?.Phone,
        _ => null,
    };

    private async void OnEditClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync($"{EditCardiMemberPage.Route}?memberId={_route.Id}");

    private async void OnWeatherTapped(object? sender, TappedEventArgs e)
    {
        if (_member?.Weather is { } weather)
            await _popups.ShowWeatherAsync(weather);
    }

    /// <summary>
    /// The freshness dot's own explanation. The dot alone says "something about the data" in a
    /// colour; the popup it opens carries that same colour, says when the data last arrived and
    /// gives the pipeline's word for the state — what the line beside the dot used to carry,
    /// before the corner took the job.
    /// </summary>
    private async void OnSyncStatusTapped(object? sender, TappedEventArgs e)
    {
        if (_member is not { } member)
            return;

        await _popups.ShowSyncStatusAsync(
            member.DataFreshness, member.DataFreshnessMessage, member.LastSyncedAt);
    }

    /// <summary>
    /// When the data last arrived, in both the forms a caregiver reads: how long ago, and the
    /// clock time it happened at. Never-synced has no clock time to give.
    /// </summary>
    private static string LastSyncedSummary(CardiMemberDetailResponse member) =>
        member.LastSyncedAt is { } lastSynced
            ? $"Last synced {RelativeTime.Format(lastSynced)}, at "
              + $"{DateTime.SpecifyKind(lastSynced, DateTimeKind.Utc).ToLocalTime():MMM d, h:mm tt}."
            : "This CardiMember has not synced yet.";

    /// <summary>
    /// The Management group's per-member words: what the group is for, the lines under the
    /// device and pause rows, and the two rows that name the member. Only facts this response
    /// already carries — a line the page would have to fetch or guess is left off (see the
    /// comment on the group in the XAML).
    /// </summary>
    private void ApplyManagementLines(CardiMemberDetailResponse member)
    {
        var name = string.IsNullOrWhiteSpace(member.FirstName) ? null : member.FirstName.Trim();

        ManagementSubtitle.Text = $"How CardiTrack watches over {name ?? "them"}";
        WhoCanSeeLabel.Text = $"Who Can See {name ?? "Them"}";
        RemoveMemberLabel.Text = name is null ? "Remove CardiMember" : $"Remove {name}";

        DeviceStatusLabel.Text = member.LastSyncedAt is { } lastSynced
            ? $"Last synced {RelativeTime.Format(lastSynced)}"
            : "Not synced yet";
        DeviceStatusLabel.IsVisible = true;

        PauseStatusLabel.Text = member.MonitoringPaused
            ? member.MonitoringPausedUntil is { } until
                ? $"Paused until {DateTime.SpecifyKind(until, DateTimeKind.Utc).ToLocalTime():MMM d, h:mm tt}"
                : "Paused until further notice"
            : "Monitoring is on";
        PauseStatusLabel.IsVisible = true;
    }

    private async void OnManageDevicesTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync($"{DeviceManagementPage.Route}?memberId={_route.Id}");

    /// <summary>This member's daybook — the tab, already filtered to them, with the origin
    /// remembered so back returns here rather than to wherever the tab was last left.</summary>
    private async void OnDaybookTapped(object? sender, EventArgs e) =>
        await Shell.Current.GoToTabAsync($"{AppShell.JournalRoute}?memberId={_route.Id}");

    /// <summary>When this member's books are written. The name rides along so the page's
    /// subtitle is right from the first frame, the way the journal entry page takes it.</summary>
    private async void OnJournalTimingTapped(object? sender, EventArgs e)
    {
        var name = Uri.EscapeDataString(_member?.DisplayFirstName() ?? string.Empty);
        await Shell.Current.GoToAsync(
            $"{JournalTimingPage.Route}?memberId={_route.Id}&name={name}");
    }

    /// <summary>M1-17 Health Data Export, scoped to the member whose page this is.</summary>
    /// <summary>
    /// "Who can see &lt;name&gt;" — the caregivers watching this member and the invitations out
    /// for them. The member's name rides along so the page can title itself without a second
    /// fetch of a profile this page already holds.
    /// </summary>
    private async void OnWhoCanSeeTapped(object? sender, TappedEventArgs e)
    {
        if (_member is not { } member)
            return;

        await Shell.Current.GoToAsync(
            $"{CaregiverInvitesPage.Route}?memberId={member.Id}&name={Uri.EscapeDataString(member.DisplayFirstName())}");
    }

    private async void OnExportDataTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync($"{ExportHealthDataPage.Route}?memberId={_route.Id}");

    private async void OnQuestionsTapped(object? sender, EventArgs e)
    {
        // The questions screen is pushed over this page rather than replacing it, so coming back
        // returns to this instance with whatever was on it still up — including a question that
        // was answered over there. The cadence must not hold that card for the rest of its
        // window, so the return is told to read the questions whatever the clock says.
        _questionsChangedElsewhere = true;

        var name = Uri.EscapeDataString(_member?.DisplayFirstName() ?? string.Empty);
        await Shell.Current.GoToAsync(
            $"{QuestionnairesPage.Route}?memberId={_route.Id}&name={name}");
    }

    private void OnChatTapped(object? sender, EventArgs e) =>
        MemberChatLauncher.ShowOverlay(RootGrid, _route.Id, (_member?.DisplayFirstName() ?? string.Empty));

    private async void OnViewAlertsClicked(object? sender, EventArgs e) =>
        // Naming the member is what lets back come back to *this* page rather than to whichever
        // member the dashboard would resolve on its own.
        await Shell.Current.GoToTabAsync(AppShell.AlertsRoute, $"memberId={_route.Id}");

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
                $"Only {_member.DisplayFirstName()}'s primary caregiver can pause monitoring.", "Not your call to make");
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
            await _api.ResumeMonitoringAsync(_route.Id);
            await LoadAsync(LoadTrigger.Requested);
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't change monitoring");
            await LoadAsync(LoadTrigger.Requested);
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
            var firstName = _member.DisplayFirstName();
            var confirmed = await _popups.ConfirmWarningAsync(
                $"We'll stop collecting {firstName}'s health data and won't raise alerts until then.",
                $"Pause for {label}?",
                "Yes, pause");
            if (!confirmed)
                return;

            _member = null;
            await _api.PauseMonitoringAsync(_route.Id, new PauseMonitoringRequest { DurationHours = hours });
            await LoadAsync(LoadTrigger.Requested);
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't change monitoring");
            await LoadAsync(LoadTrigger.Requested);
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

        // The row now carries the card's side padding itself, so the list is measured at the
        // width it will actually have inside it.
        var width = (PauseRowLayout.Width > 0 ? PauseRowLayout.Width : Width)
                    - PauseRowLayout.Padding.HorizontalThickness;
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
                $"Only {_member.DisplayFirstName()}'s primary caregiver can remove them.", "Not your call to make");
            return;
        }

        var firstName = _member.DisplayFirstName();
        var confirmed = await _popups.ConfirmWarningAsync(
            $"Monitoring stops immediately and {firstName}'s devices are disconnected. " +
            "Their health history is kept for the retention period.",
            $"Remove {_member.DisplayFirstName()}?",
            "Yes, remove",
            "Keep them");
        if (!confirmed)
            return;

        _isBusy = true;
        try
        {
            await _api.RemoveCardiMemberAsync(_route.Id);
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
            await _popups.ShowErrorAsync(ex.Message, $"Couldn't remove {(_member?.DisplayFirstName() ?? string.Empty)}");
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
