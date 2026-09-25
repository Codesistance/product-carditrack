using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Family;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Onboarding;
using CardiTrack.Mobile.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile;

/// <summary>
/// The Family tab (Story 4.9): the family a caregiver is looking at, who else is in it, and —
/// for its admin — who is asking to join and the Family ID to hand them.
/// </summary>
/// <remarks>
/// <para>
/// One page, three states, chosen by <see cref="FamilyTabState"/>: no family, a member of one,
/// or its admin. Three pages would have been three copies of the roster.
/// </para>
/// <para>
/// The drawer's choice scopes this tab and nothing else (§3 of the PRD, and OQ-16): Dashboard,
/// Alerts and Journal go on showing every member the caregiver has a grant for, whichever family
/// owns them. The chosen family is remembered in preferences so switching survives the tab being
/// rebuilt, which Shell does on every navigation to it.
/// </para>
/// </remarks>
public partial class FamilyPage : ContentPage
{
    public const string Route = "family";

    /// <summary>Which family the drawer last chose. Per install, not per family — there is one tab.</summary>
    private const string SelectedFamilyKey = "FamilyTab.SelectedOrganizationId";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;

    private IReadOnlyList<FamilySummary> _families = [];
    private IReadOnlyList<FamilyJoinRequestSummary> _asks = [];
    private IReadOnlyList<CardiMemberResponse> _members = [];
    private IReadOnlyList<AlertSummaryResponse> _openAlerts = [];
    private FamilyTabSelection _selection = new(FamilyTabMode.NoFamily, null);
    private Guid? _chosen;
    private bool _busy;
    private bool _returningFromPopup;
    private DateTime _lastLoadedUtc = DateTime.MinValue;

    public FamilyPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        _feedback = new RefreshFeedback(SavedBanner, Updating);
        _chosen = Preferences.Default.Get(SelectedFamilyKey, string.Empty) is { Length: > 0 } stored
            && Guid.TryParse(stored, out var id)
                ? id
                : null;

        StartFamilyDetailLabel.Text = FamilyCopy.TrialStartsWithFirstMember;

        // Re-tapping the tab while already on it opens the drawer (D-19). The bar swallows that
        // tap rather than rebuilding the page, so it is raised here instead.
        //
        // Subscribed and unsubscribed with the page, not in this constructor: the event is
        // process-wide and static, Shell builds a fresh FamilyPage on every navigation to the
        // tab, and a handler left attached roots that page, its view tree and its API client for
        // the rest of the session. The IsOnScreen test in the handler stops a stale instance
        // acting; it does nothing about it still being there.
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
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
    }

    private Task RefreshUnattendedAsync() =>
        DateTime.UtcNow - _lastLoadedUtc < ResumeRefresh.MinimumGap
            ? Task.CompletedTask
            : LoadAsync(silent: true);

    private void OnPageLoaded(object? sender, EventArgs e) =>
        BottomNavBar.SameTabTapped += OnSameTabTapped;

    private void OnPageUnloaded(object? sender, EventArgs e) =>
        BottomNavBar.SameTabTapped -= OnSameTabTapped;

    private void OnSameTabTapped(object? sender, NavTab tab)
    {
        if (tab == NavTab.Family && ScreenRefresh.IsOnScreen(this))
            _ = ShowSwitcherSafelyAsync();
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.DashboardRoute);

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    /// <summary>
    /// Everything the tab draws, in one pass: the families, the asks outstanding, and the open
    /// alerts the drawer colours its rows with. The roster and the queue belong to whichever
    /// family is selected, so they are fetched after that is decided.
    /// </summary>
    private async Task LoadAsync(bool silent = false)
    {
        if (_gate.IsLoading)
            return;
        var ticket = _gate.Begin();

        var cold = ContentPanel.IsVisible is false;
        if (cold)
            SetState(loading: true);

        try
        {
            var outcome = await SnapshotRefresh.RunAsync(
                _api, _gate, ticket,
                peek: cold ? (ct, scope) => PeekAsync(scope, ct) : null,
                fetch: FetchAsync,
                render: view =>
                {
                    try
                    {
                        Apply(view);
                    }
                    catch (Exception ex)
                    {
                        // A payload this build cannot draw must not leave the tab on its
                        // skeleton for good. The state goes to loaded either way — the panels
                        // Apply did fill are real — and the failure is recorded rather than
                        // silently swallowed.
                        ServiceHelper.GetRequiredService<ILogger<FamilyPage>>()
                            .LogError(ex, "Drawing the Family tab failed.");
                    }

                    SetState(loaded: true);
                },
                _feedback);

            switch (outcome.Result)
            {
                case RefreshResult.Superseded:
                    return;
                case RefreshResult.NothingAndFailed:
                    ErrorDetailLabel.Text = outcome.Error!.Message;
                    SetState(error: true);
                    return;
            }

            if (outcome.IsFresh)
                _lastLoadedUtc = DateTime.UtcNow;
            else if (!silent && outcome.Error is not null)
                await _popups.ShowWarningAsync(outcome.Error.Message, "Couldn't refresh");
        }
        finally
        {
            if (_gate.IsCurrent(ticket))
                Refresher.IsRefreshing = false;
            _gate.Release(ticket);
        }
    }

    /// <summary>
    /// The saved answers, so a tab opened offline has something on it. The roster and queue are
    /// peeked for whichever family the stored choice names — the same one the live pass will pick
    /// unless the caregiver has since left it.
    /// </summary>
    private async Task<FamilyView?> PeekAsync(RefreshScope scope, CancellationToken ct)
    {
        var families = await scope.Track(_api.PeekMyFamiliesAsync(ct));
        if (families is null)
            return null;

        var asks = await scope.Track(_api.PeekMyJoinRequestsAsync(ct)) ?? [];
        var selection = FamilyTabState.Resolve(families, _chosen);
        var roster = selection.Family is { } family
            ? await scope.Track(_api.PeekFamilyMembersAsync(family.OrganizationId, ct)) ?? []
            : [];
        var queue = selection.Mode == FamilyTabMode.Admin && selection.Family is { } admin
            ? await scope.Track(_api.PeekPendingJoinRequestsAsync(admin.OrganizationId, ct)) ?? []
            : [];
        var members = await scope.Track(_api.PeekCardiMembersAsync(ct)) ?? [];
        var alerts = await scope.Track(_api.PeekAlertsAsync(
            status: OfflineReadDefaults.OpenAlertStatus, limit: AlertQuery.MaxLimit, ct: ct));

        return new FamilyView(families, asks, selection, roster, queue, members, alerts?.Alerts ?? []);
    }

    private async Task<FamilyView> FetchAsync(CancellationToken ct, RefreshScope scope)
    {
        var families = await scope.Track(_api.GetMyFamiliesAsync(ct));
        var asks = await scope.Track(_api.GetMyJoinRequestsAsync(ct));
        var selection = FamilyTabState.Resolve(families, _chosen);

        var roster = selection.Family is { } family
            ? await scope.Track(_api.GetFamilyMembersAsync(family.OrganizationId, ct))
            : [];

        // Only an admin may read the queue, and asking as a member is a 403 that would fail the
        // whole load for a card that would not be drawn.
        var queue = selection.Mode == FamilyTabMode.Admin && selection.Family is { } admin
            ? await scope.Track(_api.GetPendingJoinRequestsAsync(admin.OrganizationId, ct))
            : [];

        // The members the caller may see, each stamped with the family that owns it — the join
        // that puts a member's row under the right family and its alerts on that row.
        var members = families.Count == 0 ? [] : await scope.Track(_api.GetCardiMembersAsync(ct));

        // The drawer's per-family alert state has no endpoint of its own (FamilyAlertState says
        // why), so it is derived from the open alerts this caregiver can already see. The
        // largest page the API will serve, not its default 50: the drawer counts and ranks from
        // this one response, and a page that stopped short would rank the wrong family first
        // without saying so. A caregiver with more open alerts than that has a bigger problem
        // than the drawer's ordering, and the row's count still says how many it counted.
        var alerts = families.Count == 0
            ? new AlertListResponse()
            : await scope.Track(_api.GetAlertsAsync(
                status: OfflineReadDefaults.OpenAlertStatus, limit: AlertQuery.MaxLimit, ct: ct));

        return new FamilyView(families, asks, selection, roster, queue, members, alerts.Alerts);
    }

    private void Apply(FamilyView view)
    {
        _families = view.Families;
        _asks = view.Asks;
        _members = view.Members;
        _openAlerts = view.OpenAlerts;
        _selection = view.Selection;

        var family = view.Selection.Family;
        FamilyNameLabel.Text = family?.Name ?? "Family";
        NoFamilyPanel.IsVisible = view.Selection.Mode == FamilyTabMode.NoFamily;
        FamilyPanel.IsVisible = !NoFamilyPanel.IsVisible;

        if (NoFamilyPanel.IsVisible)
        {
            HeaderSubtitle.Text = "You're not in a family yet";
            ApplyPendingAsks();
            return;
        }

        var isAdmin = view.Selection.Mode == FamilyTabMode.Admin;
        HeaderSubtitle.Text = FamilyCopy.PeopleLine(family!.MemberCount);
        RoleLabel.Text = isAdmin ? "You run this family" : "You're in this family";
        RoleDetailLabel.Text = isAdmin
            ? "You decide who joins and what they can see, and this family's plan is yours."
            : AdminLine(view.Roster);

        // Who the caller can see is the "Who we watch" list further down, by name and with each
        // one's open alert on it. Saying it again here as a sentence was the same fact twice, and
        // the weaker of the two.
        WatchedLabel.IsVisible = false;

        // Only when there is one to show. A server that predates the Family ID on this response
        // sends nothing, and a card headed "Your Family ID" with an empty line under it is worse
        // than no card — it reads as a family that has lost its code.
        var familyId = FamilyIdOf(family);
        FamilyIdCard.IsVisible = isAdmin && familyId.Length > 0;
        if (FamilyIdCard.IsVisible)
            FamilyIdLabel.Text = FamilyIdentifier.ToDisplay(familyId);

        ApplyQueue(view.Queue, isAdmin);
        ApplyRoster(view.Roster, isAdmin, family.OrganizationId);
        ApplyWatched(family, isAdmin);

        // An admin cannot simply leave (D-13): the button stays, and the refusal explains itself
        // rather than the row vanishing and leaving nothing to ask about.
        LeaveButton.Text = isAdmin ? "Hand over and leave" : "Leave this family";
        _ = ApplyNightCoverageAsync();
    }

    private static string AdminLine(IReadOnlyList<FamilyMemberSummary> roster)
    {
        var admin = roster.FirstOrDefault(m => FamilyTabState.IsAdmin(m.Role));
        return admin is null
            ? "Somebody else runs this family."
            : $"{admin.Name} runs this family and pays for it.";
    }

    /// <summary>
    /// The Family ID an admin reads out, as stored — <c>FamilyIdentifier.ToDisplay</c> puts the
    /// hyphen in for reading.
    /// </summary>
    private static string FamilyIdOf(FamilySummary family) => family.FamilyId ?? string.Empty;

    private void ApplyPendingAsks()
    {
        PendingAskHost.Clear();
        foreach (var ask in _asks)
            PendingAskHost.Add(AskCard(ask));
    }

    private View AskCard(FamilyJoinRequestSummary ask)
    {
        var (title, detail) = FamilyCopy.JoinRequestLine(ask, DateTime.UtcNow);
        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Add(new Label { Text = title, Style = Named("DashboardSectionTitle") });
        stack.Add(new Label { Text = detail, Style = Named("Body2"), LineBreakMode = LineBreakMode.WordWrap });

        var button = new Button
        {
            Text = FamilyCopy.CanAskAgain(ask) ? "Ask again" : "Withdraw",
            Style = Named(FamilyCopy.CanAskAgain(ask) ? "SecondaryOutlineButton" : "DangerOutlineButton"),
        };
        if (FamilyCopy.CanAskAgain(ask))
            button.Clicked += (_, _) => JoinEntry.Focus();
        else
            button.Clicked += (_, _) => _ = WithdrawAsync(ask);
        stack.Add(button);

        return Card(stack);
    }

    private void ApplyQueue(IReadOnlyList<PendingJoinRequest> queue, bool isAdmin)
    {
        QueueSection.IsVisible = isAdmin && queue.Count > 0;
        QueueHost.Clear();
        if (!QueueSection.IsVisible)
            return;

        QueueTitleLabel.Text = queue.Count == 1
            ? "Somebody wants to join"
            : $"{queue.Count} people want to join";

        foreach (var request in queue)
        {
            var stack = new VerticalStackLayout { Spacing = 10 };
            stack.Add(new Label { Text = request.Name, Style = Named("Body1SemiBoldDark") });
            stack.Add(new Label
            {
                Text = $"{request.Email}\nAsked {RelativeTime.Format(request.RequestedAt)}",
                Style = Named("Body2"),
                LineBreakMode = LineBreakMode.WordWrap,
            });

            var actions = new Grid
            {
                ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
                ColumnSpacing = 10,
            };
            var decline = new Button { Text = "Decline", Style = Named("SecondaryOutlineButton") };
            decline.Clicked += (_, _) => _ = DeclineAsync(request);
            var review = new Button { Text = "Review", Style = Named("PrimaryGradientButton") };
            review.Clicked += (_, _) => _ = ReviewAsync(request);
            actions.Add(decline, 0, 0);
            actions.Add(review, 1, 0);
            stack.Add(actions);

            QueueHost.Add(Card(stack));
        }
    }

    private void ApplyRoster(IReadOnlyList<FamilyMemberSummary> roster, bool isAdmin, Guid organizationId)
    {
        RosterHost.Clear();
        foreach (var person in roster)
        {
            var row = new Grid
            {
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                ColumnSpacing = 12,
                MinimumHeightRequest = 52,
            };

            var avatar = new MemberAvatar { BoxWidth = 40, VerticalOptions = LayoutOptions.Center };
            avatar.Apply(person.Name, null);
            row.Add(avatar, 0, 0);

            var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
            text.Add(new Label
            {
                Text = person.IsYou ? $"{person.Name} (you)" : person.Name,
                Style = Named("Body1SemiBoldDark"),
            });
            text.Add(new Label
            {
                Text = FamilyTabState.IsAdmin(person.Role)
                    ? "Admin · runs the family and pays"
                    : $"Member · joined {person.JoinedDate:d MMM yyyy}",
                Style = Named("Body2"),
                LineBreakMode = LineBreakMode.WordWrap,
            });
            row.Add(text, 1, 0);

            // Only an admin removes anybody, and never themselves — leaving is its own act with
            // its own rule (D-13), and the button below says so.
            if (isAdmin && !person.IsYou)
            {
                var remove = new ImageButton
                {
                    Source = "icon_person_remove.svg",
                    WidthRequest = 22,
                    HeightRequest = 22,
                    BackgroundColor = Colors.Transparent,
                    VerticalOptions = LayoutOptions.Center,
                };
                SemanticProperties.SetDescription(remove, $"Remove {person.Name}");
                remove.Clicked += (_, _) => _ = RemoveAsync(organizationId, person);
                row.Add(remove, 2, 0);
            }

            RosterHost.Add(Card(row, padding: new Thickness(14, 10)));
        }
    }

    /// <summary>
    /// The people this family watches: the caller's members that this family owns, each with its
    /// worst open alert. The caregiver count is not on the wire, so the row says what is known
    /// rather than inventing a number. Tapping one lands on their alert, which is what D-19's
    /// last edge asks for: a family the drawer showed as red must be one tap from the thing that
    /// made it red — and with no open alert, on the member themselves.
    /// </summary>
    /// <remarks>
    /// Joined by id, never by name. A family that watches the same person as another family
    /// holds its own record (D-15), and a row matched on the name could show the other family's
    /// alert and open the other family's member.
    /// </remarks>
    /// <summary>
    /// "Who we watch": one card per CardiMember — photo, name, and when their device last sent
    /// anything — and, for the admin, a card to add another.
    /// </summary>
    /// <remarks>
    /// A person, not an alert. These cards used to lead with the member's worst open alert and a
    /// severity pill, which made the list a second, smaller Alerts tab; what a caregiver scanning
    /// the family wants here is whether everybody's device is still talking to us, and the alerts
    /// have their own tab. The add card sits under the members because this is where a family
    /// grows: until it existed, adding a second person was reachable only through "Start a
    /// family", which said it did something else.
    /// </remarks>
    private void ApplyWatched(FamilySummary family, bool isAdmin)
    {
        WatchedHost.Clear();
        var members = _members.Where(m => m.OrganizationId == family.OrganizationId).ToList();
        WatchedSection.IsVisible = members.Count > 0 || isAdmin;
        if (!WatchedSection.IsVisible)
            return;

        var now = DateTime.UtcNow;
        foreach (var member in members)
            WatchedHost.Add(MemberCard(member, now));

        // Admin only: adding a CardiMember is a decision about the family's plan, and the admin is
        // the one who holds it.
        if (isAdmin)
            WatchedHost.Add(AddMemberCard());
    }

    private Border MemberCard(CardiMemberResponse member, DateTime now)
    {
        var row = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 12,
            MinimumHeightRequest = 56,
        };

        var avatar = new MemberAvatar { BoxWidth = 48, VerticalOptions = LayoutOptions.Center };
        avatar.Apply(member.Name, member.PhotoUrl);
        row.Add(avatar, 0, 0);

        var (syncText, freshness) = MemberSyncLine.For(member.LastSyncedAt, member.ConnectedDeviceCount, now);

        // The dot carries how worrying the silence is; the words carry how long. No dot at all for
        // "no device connected" — that is a set-up step, not a fault, and a grey dot beside it
        // read as a device that had died.
        var syncLine = new HorizontalStackLayout { Spacing = 6 };
        if (freshness != SyncFreshness.NoDevice)
        {
            syncLine.Add(new Ellipse
            {
                WidthRequest = 8,
                HeightRequest = 8,
                Fill = new SolidColorBrush(MetricStatus.Resource(freshness switch
                {
                    SyncFreshness.Recent => "StatusGreen",
                    SyncFreshness.Quiet => "StatusYellow",
                    _ => "StatusOrange",
                }, Colors.Gray)),
                VerticalOptions = LayoutOptions.Center,
            });
        }
        syncLine.Add(new Label
        {
            Text = syncText,
            Style = Named("Body2"),
            LineBreakMode = LineBreakMode.TailTruncation,
        });

        var text = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        text.Add(new Label { Text = member.Name, Style = Named("Body1SemiBoldDark") });
        text.Add(syncLine);
        row.Add(text, 1, 0);

        row.Add(new Image
        {
            Source = "icon_chevron.svg",
            WidthRequest = 20,
            HeightRequest = 20,
            VerticalOptions = LayoutOptions.Center,
        }, 2, 0);

        var card = Card(row, padding: new Thickness(14, 12));
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
            await Shell.Current.GoToAsync($"{CardiMemberDetailPage.Route}?memberId={member.Id}");
        card.GestureRecognizers.Add(tap);
        SemanticProperties.SetDescription(card, $"{member.Name}, {syncText}");
        return card;
    }

    private Border AddMemberCard()
    {
        var row = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            ColumnSpacing = 12,
            MinimumHeightRequest = 56,
        };

        // The member card's 48 box, holding a plus where the photo would be, so the add card
        // reads as the next member in the list rather than as a button dropped under it.
        var tile = new Border
        {
            WidthRequest = 48,
            HeightRequest = 48,
            StrokeThickness = 0,
            BackgroundColor = MetricStatus.Resource("MetricTileTint", Colors.LightBlue),
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = new Image
            {
                Source = "icon_plus.svg",
                WidthRequest = 22,
                HeightRequest = 22,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            },
        };
        row.Add(tile, 0, 0);
        row.Add(new Label
        {
            Text = "Add someone to watch",
            Style = Named("Body1SemiBoldDark"),
            TextColor = MetricStatus.Resource("PrimaryDark", Colors.DarkBlue),
            VerticalOptions = LayoutOptions.Center,
        }, 1, 0);

        var card = Card(row, padding: new Thickness(14, 12));
        var tap = new TapGestureRecognizer();
        tap.Tapped += OnAddMemberTapped;
        card.GestureRecognizers.Add(tap);
        SemanticProperties.SetDescription(card, "Add someone to watch");
        return card;
    }

    private bool _wizardActive;

    /// <summary>
    /// The add wizard, as the dashboard runs it for a family's first member. It remembers the new
    /// member as the one the dashboard shows (AddCardiMemberPage), so "Go to Dashboard" lands on
    /// the person just added; staying here reloads the list so they appear in it.
    /// </summary>
    private async void OnAddMemberTapped(object? sender, TappedEventArgs e)
    {
        if (_wizardActive)
            return;
        _wizardActive = true;
        try
        {
            var result = await WizardLauncher.RunModalAsync(Navigation, member: null);
            if (result.ExitedToDashboard)
                return;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // async void: anything escaping takes the app down rather than reaching a caller.
            await _popups.ShowErrorAsync(ex.Message, "Couldn't add a CardiMember");
        }
        finally
        {
            _wizardActive = false;
        }
    }

    /// <summary>
    /// What this caregiver chose when they accepted (D-8), read from their own notification
    /// preferences. Best-effort: the row hides when the preferences will not load, because a
    /// line that guesses at whether an alert will wake somebody is worse than no line.
    /// </summary>
    private async Task ApplyNightCoverageAsync()
    {
        try
        {
            var prefs = await _api.GetNotificationPreferencesAsync();
            NightCoverageLabel.Text = FamilyCopy.NightCoverageLine(prefs.EscalatedAlertsPierceQuietHours);
            NightCoverageRow.IsVisible = true;
        }
        catch (ApiException)
        {
            NightCoverageRow.IsVisible = false;
        }
    }

    private async void OnNightCoverageTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(NotificationPreferencesPage.Route);

    private void OnJoinCompleted(object? sender, EventArgs e) => _ = JoinAsync();

    private void OnJoinClicked(object? sender, EventArgs e) => _ = JoinAsync();

    /// <summary>
    /// One field, two things it can hold (D-11). A Family ID asks to join; an invitation link
    /// opens the invitation, which is a different act — it names a member and grants access on
    /// acceptance rather than asking anybody.
    /// </summary>
    private async Task JoinAsync()
    {
        if (_busy)
            return;

        var parsed = JoinInput.Parse(JoinEntry.Text);
        if (parsed is null)
        {
            await _popups.ShowWarningAsync(
                "That doesn't look like a Family ID or an invitation link. A Family ID is eight characters, like KTR7-M2Q9.",
                "Check that code");
            return;
        }

        if (parsed is JoinInput.Invitation invitation)
        {
            JoinEntry.Text = string.Empty;
            await Shell.Current.GoToAsync(
                $"{AcceptInvitePage.Route}?token={Uri.EscapeDataString(invitation.Token)}");
            return;
        }

        var code = ((JoinInput.FamilyCode)parsed).FamilyId;
        _busy = true;
        JoinButton.IsEnabled = false;
        try
        {
            await _api.RequestToJoinFamilyAsync(code);
            JoinEntry.Text = string.Empty;

            // The receipt says nothing about whether that family exists, and neither does this:
            // a message that changed with the answer would be the enumeration oracle the endpoint
            // was built to avoid.
            await _popups.ShowInfoAsync(
                "If that code is right, their admin has been asked. They'll let you know.",
                "We've sent your ask");
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't ask to join");
        }
        finally
        {
            _busy = false;
            JoinButton.IsEnabled = true;
        }
    }

    private async Task WithdrawAsync(FamilyJoinRequestSummary ask)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            await _api.WithdrawJoinRequestAsync(ask.RequestId);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't withdraw that");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ReviewAsync(PendingJoinRequest request)
    {
        if (_selection.Family is not { } family)
            return;

        _returningFromPopup = true;
        await Shell.Current.GoToAsync(
            $"{ApproveJoinRequestPage.Route}?organizationId={family.OrganizationId}&requestId={request.RequestId}"
            + $"&name={Uri.EscapeDataString(request.Name)}");
    }

    private async Task DeclineAsync(PendingJoinRequest request)
    {
        if (_selection.Family is not { } family || _busy)
            return;

        var confirmed = await _popups.ConfirmWarningAsync(
            $"{request.Name} won't be told why, and can ask again later.",
            "Say no to this request?", "Decline", "Cancel");
        if (!confirmed)
            return;

        _busy = true;
        try
        {
            await _api.DeclineJoinRequestAsync(family.OrganizationId, request.RequestId);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't decline that");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Removal, with what it does and does not do said plainly (Story 4.4): access stops now, the
    /// record of what they saw stays for the six years the audit trail keeps.
    /// </summary>
    private async Task RemoveAsync(Guid organizationId, FamilyMemberSummary person)
    {
        if (_busy)
            return;

        var confirmed = await _popups.ConfirmWarningAsync(
            $"{person.Name} loses the dashboard straight away and stops getting alerts about anyone here. "
            + "The record of what they already saw is kept for six years, as the law asks — removing them does not erase it.",
            $"Remove {NameFormatting.FirstName(person.Name)}?", "Remove", "Cancel");
        if (!confirmed)
            return;

        _busy = true;
        try
        {
            await _api.RemoveFamilyMemberAsync(organizationId, person.UserId);
            await LoadAsync();

            // Said after the fact rather than in the confirmation: it is true of the family that
            // is left, and a caregiver deciding about one person should not have to read a
            // paragraph about the others first.
            if (_selection.Family is { } family && family.WatchedMemberNames.Count > 0)
            {
                await _popups.ShowInfoAsync(
                    "Monitoring carries on under you — nobody here is left unwatched.",
                    "They no longer have access");
            }
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't remove them");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Leaving, and the two ways it is refused: an admin with people under them has to hand the
    /// family on first (D-13), and an admin alone in one is asked to delete their account
    /// instead. Both come back as a 422 whose message is the server's, so this offers the next
    /// step rather than restating the rule.
    /// </summary>
    private async void OnLeaveClicked(object? sender, EventArgs e)
    {
        if (_selection.Family is not { } family || _busy)
            return;

        var isAdmin = _selection.Mode == FamilyTabMode.Admin;
        if (isAdmin)
        {
            await Shell.Current.GoToAsync(
                $"{TransferFamilyAdminPage.Route}?organizationId={family.OrganizationId}"
                + $"&name={Uri.EscapeDataString(family.Name)}");
            return;
        }

        var confirmed = await _popups.ConfirmWarningAsync(
            $"You'll stop seeing everyone in {family.Name} and stop getting their alerts. "
            + "Their admin can invite you back.",
            $"Leave {family.Name}?", "Leave", "Stay");
        if (!confirmed)
            return;

        _busy = true;
        try
        {
            await _api.LeaveFamilyAsync(family.OrganizationId);
            Remember(null);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't leave");
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnStartFamilyClicked(object? sender, EventArgs e) =>
        _ = Shell.Current.GoToAsync(StartFamilyPage.Route);

    private async void OnCopyFamilyIdClicked(object? sender, EventArgs e)
    {
        if (_selection.Family is not { } family)
            return;

        await Clipboard.Default.SetTextAsync(FamilyIdentifier.ToDisplay(FamilyIdOf(family)));

        // Confirmed on the button that was pressed rather than in a popup to dismiss — the same
        // answer the device-invite screen gives.
        CopyFamilyIdButton.Text = "Copied";
        await Task.Delay(TimeSpan.FromSeconds(2));
        CopyFamilyIdButton.Text = "Copy";
    }

    private async void OnShareFamilyIdClicked(object? sender, EventArgs e)
    {
        if (_selection.Family is not { } family)
            return;

        try
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = "Share your Family ID",
                Subject = $"Join {family.Name} on CardiTrack",
                Text = FamilyCopy.ShareFamilyIdText(family.Name, FamilyIdOf(family)),
            });
        }
        catch (Exception)
        {
            await _popups.ShowWarningAsync("Sharing isn't supported on this device.");
        }
    }

    private void OnSwitchFamilyTapped(object? sender, TappedEventArgs e) => _ = ShowSwitcherSafelyAsync();

    /// <summary>
    /// Both ways into the drawer are fire-and-forget from a tap, so nothing above them can catch
    /// a failure: without this a drawer that could not be built would simply do nothing, twice,
    /// and leave the caregiver tapping a control that never answers.
    /// </summary>
    private async Task ShowSwitcherSafelyAsync()
    {
        try
        {
            await ShowSwitcherAsync();
        }
        catch (Exception ex)
        {
            ServiceHelper.GetRequiredService<ILogger<FamilyPage>>()
                .LogError(ex, "Opening the family switcher failed.");
            await _popups.ShowWarningAsync(ex.Message, "Couldn't open your families");
        }
    }

    /// <summary>
    /// The drawer (D-19). It opens whatever the caregiver's situation is — one family, several,
    /// or none — because the way to a second family has to stay where it was.
    /// </summary>
    private async Task ShowSwitcherAsync()
    {
        if (_popups.IsShowing)
            return;

        FamilyAlertSummary StateOf(FamilySummary f) =>
            FamilyAlertState.For(FamilyAlertState.MembersOf(f.OrganizationId, _members), _openAlerts);

        var ordered = FamilyAlertState.OrderForDrawer(_families, StateOf);
        var rows = ordered
            .Select(f => new FamilySwitcherRow(
                f.OrganizationId,
                f.Name,
                FamilyTabState.IsAdmin(f.Role) ? "Admin" : "Member",
                StateOf(f),
                f.OrganizationId == _selection.Family?.OrganizationId))
            .ToList();

        var waiting = _asks.Where(FamilyCopy.IsPending).Select(a => a.FamilyName).ToList();

        _returningFromPopup = true;
        var choice = await _popups.ChooseFamilyAsync(rows, waiting);
        _returningFromPopup = false;

        switch (choice)
        {
            case FamilySwitcherChoice.StartFamily:
                await Shell.Current.GoToAsync(StartFamilyPage.Route);
                return;
            case FamilySwitcherChoice.JoinFamily:
                // The field lives on the no-family card; a caregiver already in one is shown it
                // on its own page rather than having the tab change shape under them.
                await Shell.Current.GoToAsync(JoinFamilyPage.Route);
                return;
            case FamilySwitcherChoice.Chosen chosen:
                if (chosen.OrganizationId == _selection.Family?.OrganizationId)
                    return;
                Remember(chosen.OrganizationId);
                await LoadAsync();
                return;
        }
    }

    private void Remember(Guid? organizationId)
    {
        _chosen = organizationId;
        if (organizationId is { } id)
            Preferences.Default.Set(SelectedFamilyKey, id.ToString());
        else
            Preferences.Default.Remove(SelectedFamilyKey);
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
    }

    private static Style? Named(string key) =>
        Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var found) == true
            ? found as Style
            : null;

    private static Border Card(View content, Thickness? padding = null)
    {
        var card = new Border { Style = Named("ElevatedCard"), Content = content };
        if (padding is { } p)
            card.Padding = p;
        return card;
    }

    /// <summary>Everything one pass fetched, so the render sees a consistent picture.</summary>
    private sealed record FamilyView(
        IReadOnlyList<FamilySummary> Families,
        IReadOnlyList<FamilyJoinRequestSummary> Asks,
        FamilyTabSelection Selection,
        IReadOnlyList<FamilyMemberSummary> Roster,
        IReadOnlyList<PendingJoinRequest> Queue,
        IReadOnlyList<CardiMemberResponse> Members,
        IReadOnlyList<AlertSummaryResponse> OpenAlerts);
}
