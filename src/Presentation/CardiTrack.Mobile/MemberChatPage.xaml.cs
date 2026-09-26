using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Maui.Controls.Shapes;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Chat;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// A caregiver's persisted, multi-turn conversation about one CardiMember's readings. No Figma
/// frame — as-built, see the design-sync backlog. Always shown as an overlay layered directly
/// into whatever page launched it (Member Detail's "Ask about their readings" row, or the
/// Dashboard's ChatBot button) — see <see cref="MemberChatLauncher"/> — rather than pushed as a
/// separate modal page: MAUI's cross-platform modal push does not render the previous page
/// visible/dimmed behind a transparent one (Android in particular composites a pushed page as a
/// fully opaque screen regardless of its own BackgroundColor), so the only way to get a real
/// dimmed-background-still-visible effect is to stay in the host page's own visual tree.
/// </summary>
public partial class MemberChatPage : ContentView
{
    /// <summary>Raised when the caregiver dismisses the overlay (down button or scrim tap) — the
    /// host page removes this view from whatever layer it added it to.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>What row 0 is currently showing: the live thread, the list of past
    /// conversations, or one past conversation opened read-only from that list.</summary>
    private enum ChatViewMode { Thread, HistoryList, PastSession }

    private readonly ICardiTrackApiClient _api;
    private readonly ObservableCollection<ChatTurnItem> _turns = [];
    private readonly ObservableCollection<ChatSessionItem> _sessions = [];

    /// <summary>A reopened past conversation's bubbles — its own collection, so browsing history
    /// never touches <see cref="_turns"/> and the live thread survives the visit intact.</summary>
    private readonly ObservableCollection<ChatTurnItem> _pastTurns = [];

    /// <summary>Who the conversation is about. <see cref="Guid.Empty"/> until one is chosen when
    /// the sheet opened without one — see <see cref="ShowChooserAsync"/>.</summary>
    private Guid _memberId;
    private string? _memberFirstName;
    private string _threadSubtitle;

    /// <summary>The member's full name and photo, for the header pill's avatar — filled in once
    /// the member list has been read; until then the avatar shows the first name's initial.</summary>
    private string? _memberFullName;
    private string? _memberPhotoUrl;

    /// <summary>The signed-in caregiver's first name, for the greeting. Null when unknown.</summary>
    private readonly string? _caregiverFirstName;

    /// <summary>Which of <see cref="ChatFunFacts"/> this conversation greets with.</summary>
    private int _funFactIndex;

    /// <summary>The greeting has played for the empty conversation on screen — it plays once per
    /// conversation, not every time the empty list is laid out again.</summary>
    private bool _greetingPlayed;

    /// <summary>Where the next conversation's fact is read from, so each new one teaches
    /// something the last did not — across openings of the sheet, not only within one.</summary>
    private const string FunFactPreferenceKey = "chat.funFact.next";

    /// <summary>The "who" question is on screen in place of the thread.</summary>
    private bool _choosing;

    /// <summary>There is more than one member to chat about, so the subtitle offers a switch.</summary>
    private bool _canSwitch;
    private ChatViewMode _mode = ChatViewMode.Thread;

    /// <summary>The completed conversation currently open read-only — what the Continue button
    /// reopens. Meaningful only in <see cref="ChatViewMode.PastSession"/>.</summary>
    private Guid _viewedSessionId;

    /// <summary>The past conversation currently open, for the export action beside Continue —
    /// its label and the days it spans, which the list row already knew and a
    /// <c>MemberChatHistoryResponse</c> does not carry.</summary>
    private ChatSessionItem? _viewedSession;

    /// <summary>The live conversation on screen, or empty when there is none yet — a caregiver
    /// who has not asked anything has nothing to export.</summary>
    private Guid _currentSessionId;

    /// <summary>The local day the live conversation began, for dating an export of it. Default
    /// until a thread with turns has loaded.</summary>
    private DateOnly _currentStartedOn;

    private bool _isLoading;
    private bool _isSending;

    /// <summary>
    /// Holds the <see cref="AiChatNotice"/> scope of the caregiver who has seen the AI notice on
    /// this phone. Per caregiver for the same reason as the telemetry notice; sign-out clears it.
    /// </summary>
    internal const string AiChatNoticeSeenKey = "AiChatNoticeSeenFor";

    /// <summary>The AI notice while it is up, so the open and a quick first send share one popup.</summary>
    private Task<bool>? _aiNotice;

    /// <summary>How long a step has to be on screen before the waiting lines start under it — a
    /// step that finishes sooner has already said enough, and a line that flashes past reads as
    /// noise.</summary>
    private static readonly TimeSpan WaitingLineDelay = TimeSpan.FromSeconds(3);

    /// <summary>How long each waiting line stays before the next — long enough to read twice.</summary>
    private static readonly TimeSpan WaitingLineRotation = TimeSpan.FromSeconds(4);

    /// <summary>The last thread load failed — so a return from history retries it rather than
    /// presenting the empty list the failure left behind as a conversation.</summary>
    private bool _threadLoadFailed;

    /// <summary>The in-flight load of the live thread, if any — a send awaits it before
    /// appending, so the load's rebuild of the list cannot wipe turns added after it started.
    /// Never faults: <see cref="LoadAsync"/> handles its own failures. (Only ever a
    /// <see cref="LoadAsync"/> task — the history list's loads have no such race to guard.)</summary>
    private Task? _loadTask;

    /// <param name="memberId">
    /// Who the conversation is about, or <see cref="Guid.Empty"/> to have the sheet ask — the
    /// launcher on a page showing several members opens it that way.
    /// </param>
    /// <param name="caregiverFirstName">Who the greeting says hello to; null for no name.</param>
    public MemberChatPage(
        ICardiTrackApiClient api, Guid memberId, string? memberFirstName, string? caregiverFirstName = null)
    {
        InitializeComponent();
        _api = api;
        _memberId = memberId;
        _memberFirstName = memberFirstName;
        _caregiverFirstName = string.IsNullOrWhiteSpace(caregiverFirstName) ? null : caregiverFirstName.Trim();
        TurnsList.ItemsSource = _turns;
        SessionsList.ItemsSource = _sessions;

        _threadSubtitle = SubtitleFor(memberFirstName);
        ShowThreadSubtitle();
        _funFactIndex = TakeFunFactIndex();
        ApplyGreeting();

        // No OnAppearing on a ContentView — the host adds this to its tree only at the moment
        // it's shown (see MemberChatLauncher), so construction time is the right time to load.
        if (memberId == Guid.Empty)
        {
            _ = ShowChooserAsync();
        }
        else
        {
            _loadTask = LoadAsync();
            // Whether the subtitle may offer a switch: only worth a tap when there is somebody
            // else to switch to.
            _ = LearnWhetherSwitchableAsync();
        }

        // The AI notice is owed at the first interaction, and opening the sheet is it. Posted
        // rather than shown from the constructor: the host adds this view to its tree only after
        // constructing it.
        Dispatcher.Dispatch(() => _ = EnsureAiNoticeSeenAsync());

        // Opening chat is the strongest signal there is that a clinical read is about to be
        // needed. The login-time warm-up (PostLoginRouter) has usually long lapsed by now — the
        // medical model scales to zero after idle, and a send that finds it cold waited over a
        // minute (66 s observed in dev) for the load alone. The API debounces repeats, so
        // reopening the sheet costs nothing extra.
        _ = WarmAssistantAsync();
    }

    private async Task WarmAssistantAsync()
    {
        try
        {
            await _api.PrepareAssistantAsync();
        }
        catch (Exception)
        {
            // Invisible to the caregiver by design, like the login-time call: an offline open is
            // ordinary, and a send that follows is what reports a real problem. Started
            // fire-and-forget, so the catch is also what keeps it from surfacing as an
            // unobserved task exception.
        }
    }

    /// <summary>
    /// The live thread's header: the member pill when the conversation is about someone, the
    /// plain question when nobody has been named.
    /// </summary>
    private void ShowThreadSubtitle()
    {
        if (string.IsNullOrWhiteSpace(_memberFirstName))
        {
            ShowSubtitle(_threadSubtitle);
            return;
        }

        PillName.Text = _memberFirstName;
        PillAvatar.Apply(_memberFullName ?? _memberFirstName, _memberPhotoUrl);
        SemanticProperties.SetDescription(MemberPill, $"Chatting about {_memberFirstName}");
        SemanticProperties.SetHint(MemberPill, _canSwitch ? "Double tap to chat about someone else" : string.Empty);
        SubtitleLabel.IsVisible = false;
        MemberPill.IsVisible = true;
    }

    /// <summary>A plain subtitle, for the states that are not about one member.</summary>
    private void ShowSubtitle(string text)
    {
        SubtitleLabel.Text = text;
        SubtitleLabel.IsVisible = true;
        MemberPill.IsVisible = false;
    }

    /// <summary>
    /// The next fact in the rotation, moving the stored position on — so the one after it is
    /// what the next conversation opens with. Preferences can fail (a locked keystore); the
    /// greeting then simply starts from the first fact.
    /// </summary>
    private static int TakeFunFactIndex()
    {
        try
        {
            var index = Preferences.Default.Get(FunFactPreferenceKey, 0);
            Preferences.Default.Set(FunFactPreferenceKey, (index + 1) % ChatFunFacts.Count);
            return index;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>The greeting's words: hello by name, and this conversation's fact.</summary>
    private void ApplyGreeting()
    {
        GreetingTitle.Text = _caregiverFirstName is { } name
            ? $"Hi {name}, I'm here to help"
            : "I'm here to help";

        FunFactLabel.FormattedText = new FormattedString
        {
            Spans =
            {
                new Span
                {
                    Text = "Did you know? ",
                    FontFamily = "QuicksandSemiBold",
                    TextColor = MetricStatus.Resource("PrimaryDark", Colors.DarkBlue),
                },
                new Span { Text = ChatFunFacts.At(_funFactIndex, _memberFirstName) },
            },
        };
    }

    /// <summary>
    /// The bot springs up into place, waves — a few quick tilts on its base, settling — and the
    /// fact fades in under the hello. Once per conversation: the empty list is laid out again
    /// on every state change, and a bot that waved at each of those would be fidgeting.
    /// </summary>
    private void PlayGreeting()
    {
        if (_greetingPlayed)
            return;
        _greetingPlayed = true;

        GreetingBot.AbortAnimation("greeting");
        GreetingBot.Scale = 0.4;
        GreetingBot.Opacity = 0;
        GreetingBot.Rotation = 0;
        FunFactCard.Opacity = 0;

        var wave = new Animation();
        wave.Add(0.00, 0.30, new Animation(v => GreetingBot.Scale = v, 0.4, 1, Easing.SpringOut));
        wave.Add(0.00, 0.15, new Animation(v => GreetingBot.Opacity = v, 0, 1));
        wave.Add(0.30, 0.42, new Animation(v => GreetingBot.Rotation = v, 0, -12, Easing.SinOut));
        wave.Add(0.42, 0.56, new Animation(v => GreetingBot.Rotation = v, -12, 10, Easing.SinInOut));
        wave.Add(0.56, 0.70, new Animation(v => GreetingBot.Rotation = v, 10, -6, Easing.SinInOut));
        wave.Add(0.70, 0.82, new Animation(v => GreetingBot.Rotation = v, -6, 0, Easing.SinOut));
        wave.Add(0.55, 0.85, new Animation(v => FunFactCard.Opacity = v, 0, 1));
        wave.Commit(GreetingBot, "greeting", 16, 1800, Easing.Linear, (_, _) =>
        {
            GreetingBot.Scale = 1;
            GreetingBot.Opacity = 1;
            GreetingBot.Rotation = 0;
            FunFactCard.Opacity = 1;
        });
    }

    private static string SubtitleFor(string? firstName) =>
        string.IsNullOrWhiteSpace(firstName)
            ? "What would you like to know?"
            : $"What would you like to know about {firstName}?";

    private async Task LearnWhetherSwitchableAsync()
    {
        try
        {
            var members = await _api.GetCardiMembersAsync();
            _canSwitch = members.Count > 1;
            SwitchChevron.IsVisible = _canSwitch && !_choosing;
            if (members.FirstOrDefault(m => m.Id == _memberId) is { } member)
            {
                _memberFullName = member.Name;
                _memberPhotoUrl = member.PhotoUrl;
                if (_mode == ChatViewMode.Thread && !_choosing)
                    ShowThreadSubtitle();
            }
        }
        catch (Exception)
        {
            // No switch offered is the safe default; the conversation itself is unaffected.
        }
    }

    private async void OnSubtitleTapped(object? sender, TappedEventArgs e)
    {
        // Mid-send the thread belongs to the member being asked about; history browsing has its
        // own way out. Only a live thread with somebody else to talk about offers the switch.
        if (!_canSwitch || _choosing || _isSending || _mode != ChatViewMode.Thread)
            return;
        await ShowChooserAsync();
    }

    /// <summary>
    /// Puts the bot's "who" question in the conversation area, one row per member. A family of
    /// one is not asked: that member is chosen at once.
    /// </summary>
    private async Task ShowChooserAsync()
    {
        _choosing = true;
        SwitchChevron.IsVisible = false;
        HistoryButton.IsVisible = false;
        SuggestionsPanel.IsVisible = false;
        NewConversationAction.IsVisible = false;
        ExportThreadAction.IsVisible = false;
        MessageEditor.IsEnabled = false;
        MessageEditor.Placeholder = "Choose someone first";
        ShowSubtitle("Choose who this is about");
        SetState(loading: ChoiceList.Count == 0);

        List<CardiMemberResponse> members;
        try
        {
            members = await _api.GetCardiMembersAsync();
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while listing members to chat about");
            ErrorDetailLabel.Text = ex is ApiException api ? api.Message : "Something went wrong while showing this.";
            SetState(error: true);
            return;
        }

        _canSwitch = members.Count > 1;
        if (members.Count == 1)
        {
            await ChooseAsync(members[0]);
            return;
        }

        ChoiceList.Clear();
        _choiceStatus.Clear();
        foreach (var member in members)
            ChoiceList.Add(BuildChoiceRow(member));

        SetState();
        ChooserPanel.IsVisible = true;

        // Each row's status line fills in from what the phone already holds — the dashboard's
        // saved copy and its last status line — so the list is never held up by the network.
        foreach (var member in members)
            _ = FillChoiceStatusAsync(member);
    }

    /// <summary>Each chooser row's status dot and line, filled in after the rows are drawn.</summary>
    private readonly Dictionary<Guid, (Ellipse Dot, Label Status)> _choiceStatus = [];

    private Border BuildChoiceRow(CardiMemberResponse member)
    {
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var firstName = member.DisplayFirstName();

        var avatar = new Controls.MemberAvatar { BoxWidth = 44, VerticalOptions = LayoutOptions.Center };
        avatar.Apply(member.Name, member.PhotoUrl);

        var status = new Label
        {
            FontFamily = "Quicksand",
            FontSize = 12,
            TextColor = (Color)resources["BodyText"],
            LineBreakMode = LineBreakMode.TailTruncation,
            IsVisible = false,
        };
        var dot = new Ellipse
        {
            WidthRequest = 8,
            HeightRequest = 8,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
        };

        var current = member.Id == _memberId;
        var row = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 12,
        };
        row.Add(avatar, 0);
        row.Add(new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label
                {
                    Text = firstName,
                    FontFamily = "QuicksandSemiBold",
                    FontSize = 16,
                    TextColor = (Color)resources["HeadingText"],
                },
                new HorizontalStackLayout { Spacing = 6, Children = { dot, status } },
            },
        }, 1);
        row.Add(new Image
        {
            Source = current ? "icon_status_check.svg" : "icon_chevron.svg",
            WidthRequest = 20,
            HeightRequest = 20,
            VerticalOptions = LayoutOptions.Center,
        }, 2);

        var card = new Border
        {
            StrokeThickness = 1,
            Stroke = current ? (Color)resources["Primary"] : ((Color)resources["PrimaryDark"]).WithAlpha(0.18f),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
            BackgroundColor = (Color)resources["White"],
            Padding = new Thickness(12, 10),
            Content = row,
        };

        SemanticProperties.SetDescription(card, current ? $"{firstName}, chatting now" : firstName);
        SemanticProperties.SetHint(card, $"Double tap to chat about {firstName}");
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await ChooseAsync(member);
        card.GestureRecognizers.Add(tap);

        _choiceStatus[member.Id] = (dot, status);
        return card;
    }

    private async Task FillChoiceStatusAsync(CardiMemberResponse member)
    {
        try
        {
            var dashboard = await _api.PeekDashboardAsync(member.Id);
            string? saved = null;
            if (dashboard is not null)
            {
                saved = (await ServiceHelper.GetRequiredService<IStatusLineStore>().TryGetAsync(
                    member.Id, dashboard.HealthStatus, TimeSpan.FromHours(6)))?.Headline;
            }

            var (text, colorKey) = ChatMemberChoice.Status(
                dashboard?.HealthStatus,
                dashboard?.MonitoringPaused ?? false,
                everSynced: (dashboard?.LastSyncedAt ?? member.LastSyncedAt) is not null,
                saved);

            if (text is null || !_choiceStatus.TryGetValue(member.Id, out var parts))
                return;

            var (dot, status) = parts;
            status.Text = text;
            status.IsVisible = true;
            dot.Fill = new SolidColorBrush(
                (Color)Microsoft.Maui.Controls.Application.Current!.Resources[colorKey]);
            dot.IsVisible = true;
        }
        catch (Exception ex)
        {
            // A missing status line leaves the name alone on the row — still a valid choice.
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while reading a member's status for the chooser");
        }
    }

    /// <summary>
    /// Makes <paramref name="member"/> the one this conversation is about and loads their live
    /// thread and chips. Choosing the member already on screen just puts that thread back.
    /// </summary>
    private async Task ChooseAsync(CardiMemberResponse member)
    {
        ChooserPanel.IsVisible = false;
        _choosing = false;
        HistoryButton.IsVisible = true;
        MessageEditor.IsEnabled = true;
        MessageEditor.Placeholder = "Ask a question…";
        SwitchChevron.IsVisible = _canSwitch;

        if (member.Id == _memberId)
        {
            ShowThread();
            return;
        }

        // An earlier load still in flight belongs to the member being left; let it finish (it
        // never faults) so its result lands before the new member's thread is started, and the
        // member check in LoadAsync throws it away.
        if (_loadTask is { } pending)
            await pending;

        _memberId = member.Id;
        _memberFirstName = member.DisplayFirstName();
        _memberFullName = member.Name;
        _memberPhotoUrl = member.PhotoUrl;
        _threadSubtitle = SubtitleFor(_memberFirstName);
        _greetingPlayed = false;
        ApplyGreeting();
        _turns.Clear();
        _sessions.Clear();
        _currentSessionId = Guid.Empty;
        _currentStartedOn = default;
        _threadLoadFailed = false;
        SuggestionsRow.Clear();

        ShowThread();
        _loadTask = LoadAsync();
    }

    /// <summary>
    /// Shows the AI notice if this caregiver has not seen it — once, however many callers ask
    /// while it is up — and says whether they have now seen it. Never faults: a notice that could
    /// not be shown is <c>false</c>, and the caller decides what that stops.
    /// </summary>
    private Task<bool> EnsureAiNoticeSeenAsync() =>
        _aiNotice is { IsCompleted: false } showing ? showing : _aiNotice = ShowAiNoticeIfOwedAsync();

    private async Task<bool> ShowAiNoticeIfOwedAsync()
    {
        try
        {
            var email = ServiceHelper.GetRequiredService<IAuthService>().CurrentUserEmail;
            if (AiChatNotice.IsSeen(Preferences.Default.Get(AiChatNoticeSeenKey, string.Empty), email))
                return true;

            // PopupService attaches to the first window's page and returns without showing
            // anything when there is none, so a notice asked for then is not one the caregiver
            // saw: nothing is recorded, and the next open or send asks again.
            if (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page is null)
                return false;

            await ServiceHelper.GetRequiredService<IPopupService>()
                .ShowInfoAsync(AiChatNotice.Message, AiChatNotice.Title, AiChatNotice.AcknowledgeText);

            // Shown is seen: the notice has one answer, and closing it any other way has still
            // put the words in front of the caregiver. With no signed-in identity there is
            // nothing to remember it by, so it shows again next time — but it was seen now.
            if (AiChatNotice.SeenValueFor(email) is { } seen)
                Preferences.Default.Set(AiChatNoticeSeenKey, seen);
            return true;
        }
        catch (Exception ex)
        {
            // Fire-and-forget from the constructor: a notice that fails must not take the chat
            // with it. A send asks again, and waits for it, since the key was never written.
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while showing the AI notice");
            return false;
        }
    }

    private void OnBackTapped(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Tapping the dimmed area outside the sheet dismisses it — same convention as
    /// AppPopupPage's scrim.</summary>
    private void OnScrimTapped(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnRetryClicked(object? sender, EventArgs e)
    {
        // The error panel is shared between modes, so the retry has to redo whichever load
        // actually failed. A past conversation that failed to open retries to the list — the id
        // it needed came from there, and the list is the safe place to pick it again.
        if (_mode == ChatViewMode.Thread)
            _loadTask = LoadAsync();
        else
            _ = ShowHistoryListAsync();
    }

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        // The refresher wraps the one CollectionView both the thread and a reopened past
        // conversation render in — but it only ever refreshes the live thread, and a past
        // conversation is finished: there is nothing fresher to fetch into it.
        if (_mode != ChatViewMode.Thread)
        {
            Refresher.IsRefreshing = false;
            return;
        }

        var load = LoadAsync();
        _loadTask = load;
        await load;
        Refresher.IsRefreshing = false;
    }

    /// <summary>The header's history button: a toggle as much as a door. From the thread it opens
    /// the list, from the list it returns to the thread, and from a reopened old conversation it
    /// steps back out to the list.</summary>
    private void OnHistoryTapped(object? sender, EventArgs e)
    {
        // Not while a reply is in flight: the pending bubble belongs to the thread, and the send's
        // completion would land its answer into a view showing something else.
        if (_isSending)
            return;

        if (_mode == ChatViewMode.HistoryList)
            ShowThread();
        else
            _ = ShowHistoryListAsync();
    }

    private void OnBackToChatClicked(object? sender, EventArgs e) => ShowThread();

    private async void OnSessionTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not ChatSessionItem item)
            return;

        // In selection mode a tap picks rather than opens — the checkmark is the whole answer,
        // and accidentally opening a conversation mid-selection would throw the selection away.
        if (item.IsSelecting)
        {
            item.IsSelected = !item.IsSelected;
            UpdateDeleteSelectedLabel();
            return;
        }

        await ShowPastSessionAsync(item);
    }

    private void OnSelectSessionsTapped(object? sender, EventArgs e)
    {
        if (_sessions.Count == 0)
            return;

        foreach (var session in _sessions)
            session.IsSelecting = true;
        HistoryActions.IsVisible = false;
        SelectionActions.IsVisible = true;
        UpdateDeleteSelectedLabel();
    }

    private void OnCancelSelectTapped(object? sender, EventArgs e) => ExitSessionSelection();

    /// <summary>Back out of selection mode, dropping any selection — reached by Cancel, by
    /// leaving the history list, and by finishing a delete.</summary>
    private void ExitSessionSelection()
    {
        foreach (var session in _sessions)
            session.IsSelecting = false;
        SelectionActions.IsVisible = false;
        HistoryActions.IsVisible = true;
    }

    /// <summary>The delete pill always states the count it would act on — a caregiver deletes a
    /// number they have read, never a selection they have lost track of. With nothing picked it
    /// reads plain "Delete" and sits half-faded, and the tap handler declines.</summary>
    private void UpdateDeleteSelectedLabel()
    {
        var count = _sessions.Count(s => s.IsSelected);
        DeleteSelectedLabel.Text = count switch
        {
            0 => "Delete",
            1 => "Delete 1 conversation",
            _ => $"Delete {count} conversations",
        };
        DeleteSelectedAction.Opacity = count == 0 ? 0.5 : 1;
        // Genuinely disabled, not just dimmed — a screen reader should hear "disabled" rather
        // than land on a pill that silently does nothing.
        DeleteSelectedAction.IsEnabled = count > 0;
    }

    /// <summary>
    /// The one permanently destructive act on this sheet, so it is the one that warns first: the
    /// confirmation says plainly that deletion cannot be undone, and nothing is sent until the
    /// caregiver agrees. The server takes at most 100 ids per call, so a bigger selection goes
    /// in batches, and rows leave the list as each batch lands — a failure keeps only what is
    /// actually still on the server selected, so a retry is one tap, not a re-pick.
    /// </summary>
    private async void OnDeleteSelectedTapped(object? sender, EventArgs e)
    {
        var selected = _sessions.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        var noun = selected.Count == 1 ? "this conversation" : $"these {selected.Count} conversations";
        bool confirmed;
        try
        {
            confirmed = await Services.ServiceHelper.GetRequiredService<Services.IPopupService>()
                .ConfirmWarningAsync(
                    $"This permanently deletes {noun}, including every answer and chart in "
                    + "them. This can't be undone.",
                    "Delete forever?",
                    "Delete",
                    "Keep");
        }
        catch (Exception ex)
        {
            // async void, reached from a gesture — a confirm that will not open must not take
            // the app down, and without an answer nothing is deleted. Logged like the delete
            // failure below so a broken confirm is diagnosable in the field.
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while confirming a delete");
            return;
        }

        if (!confirmed)
            return;

        try
        {
            foreach (var batch in selected.Chunk(MemberChatDeleteSessionsRequest.MaxBatchSize))
            {
                await _api.DeleteMemberChatSessionsAsync(
                    _memberId, batch.Select(s => s.SessionId).ToList());

                foreach (var session in batch)
                    _sessions.Remove(session);

                // Keep the pill honest between batches: if a later one fails, the count it
                // shows for the retry is what is actually still selected.
                UpdateDeleteSelectedLabel();
            }

            ExitSessionSelection();
            SelectSessionsAction.IsVisible = _sessions.Count > 0;
        }
        catch (ApiException ex)
        {
            await Services.ServiceHelper.GetRequiredService<Services.IPopupService>()
                .ShowErrorAsync(ex.Message, "Couldn't delete");
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while deleting conversations");
            await Services.ServiceHelper.GetRequiredService<Services.IPopupService>()
                .ShowErrorAsync("Something went wrong while deleting.", "Couldn't delete");
        }
    }

    /// <summary>
    /// Reopens the past conversation on screen as the live one. Server-side this clears its
    /// ended mark, brings its activity to now, and ends whatever was active — one live
    /// conversation per member — so the thread the caregiver continues is exactly the one they
    /// are looking at.
    /// </summary>
    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        if (_mode != ChatViewMode.PastSession)
            return;

        var sessionId = _viewedSessionId;
        SetState(loading: true);
        ContinuePanel.IsVisible = false;

        try
        {
            var history = await _api.ContinueMemberChatSessionAsync(_memberId, sessionId);

            _turns.Clear();
            _currentSessionId = history.SessionId;
            _currentStartedOn = history.Turns.Count > 0
                ? DateOnly.FromDateTime(history.Turns[0].CreatedAtUtc.ToLocalTime().DateTime)
                : DateOnly.FromDateTime(DateTime.Now);
            foreach (var turn in history.Turns)
                _turns.Add(ChatTurnItem.FromHistory(turn, _memberFirstName));
            _threadLoadFailed = false;

            ShowThread();
        }
        catch (ApiException ex)
        {
            if (_mode != ChatViewMode.PastSession)
                return;
            ErrorDetailLabel.Text = ex.Message;
            SetState(error: true);
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while continuing a past conversation");
            if (_mode != ChatViewMode.PastSession)
                return;
            ErrorDetailLabel.Text = "Something went wrong while showing this.";
            SetState(error: true);
        }
    }

    /// <summary>
    /// A chart carousel's edge arrow: advance one chart the way a swipe would, wrap-around
    /// included. The carousel is found by walking up from the tapped disc to the Grid that
    /// holds them both rather than by name — the pair live inside a DataTemplate, whose
    /// namescope is per-bubble, so there is no field for the code-behind to hold.
    /// </summary>
    private static void MoveChartCarousel(object? sender, int direction)
    {
        CarouselView? carousel = null;
        for (var element = sender as Element; element is not null; element = element.Parent)
        {
            if (element is Grid grid &&
                (carousel = grid.Children.OfType<CarouselView>().FirstOrDefault()) is not null)
                break;
        }

        if (carousel?.ItemsSource is not IReadOnlyList<ChatChartItem> items || items.Count < 2)
            return;

        carousel.Position = (carousel.Position + direction + items.Count) % items.Count;
    }

    private void OnChartPrevTapped(object? sender, TappedEventArgs e) => MoveChartCarousel(sender, -1);

    private void OnChartNextTapped(object? sender, TappedEventArgs e) => MoveChartCarousel(sender, +1);

    /// <summary>
    /// Ends the current conversation and clears the window for a fresh one. The ended
    /// conversation appears in the history list immediately — nothing is lost, it has just
    /// finished.
    /// </summary>
    private async void OnNewConversationTapped(object? sender, EventArgs e)
    {
        if (_mode != ChatViewMode.Thread || _isSending || _turns.Count == 0)
            return;

        NewConversationAction.IsVisible = false;
        ExportThreadAction.IsVisible = false;

        try
        {
            await _api.EndCurrentMemberChatSessionAsync(_memberId);
        }
        catch (Exception ex)
        {
            // The conversation keeps working either way: an end that failed leaves the thread
            // exactly as it was, and the action reappears for another try.
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while ending the conversation");
            UpdateNewConversationAction();
            return;
        }

        _turns.Clear();
        _currentSessionId = Guid.Empty;
        _currentStartedOn = default;
        _ = LoadSuggestionsAsync();
        UpdateNewConversationAction();

        // A new conversation is greeted afresh, with the next thing the assistant can do.
        _funFactIndex = TakeFunFactIndex();
        _greetingPlayed = false;
        ApplyGreeting();
        PlayGreeting();
    }

    /// <summary>
    /// Saves or shares a copy of the live conversation. The caregiver picks a format, confirms
    /// they accept responsibility for the copy — the same step-up every export takes — and the
    /// file arrives through the OS share sheet. See <see cref="IChatTranscriptExportFlow"/>.
    /// </summary>
    private void OnExportThreadTapped(object? sender, EventArgs e)
    {
        // Not mid-send: the reply on its way is part of the conversation, and a copy taken
        // without it would be missing the answer the caregiver is exporting it for.
        if (_mode != ChatViewMode.Thread || _isSending || _currentSessionId == Guid.Empty)
            return;

        // Dated from the conversation's first turn to today: the thread on screen is the live
        // one, so its last turn is however recently the caregiver was just talking to it.
        var today = DateOnly.FromDateTime(DateTime.Now);
        _ = ExportAsync(
            _currentSessionId,
            label: null,
            _currentStartedOn == default ? today : _currentStartedOn,
            today);
    }

    /// <summary>The same export, for the finished conversation open from the history list.</summary>
    private void OnExportPastSessionTapped(object? sender, EventArgs e)
    {
        if (_mode != ChatViewMode.PastSession || _viewedSession is not { } session)
            return;

        _ = ExportAsync(session.SessionId, session.Title, session.StartedOn, session.LastTurnOn);
    }

    private async Task ExportAsync(Guid sessionId, string? label, DateOnly started, DateOnly lastTurn)
    {
        try
        {
            await Services.ServiceHelper.GetRequiredService<IChatTranscriptExportFlow>()
                .RunAsync(sessionId: sessionId,
                    memberId: _memberId,
                    memberName: _memberFirstName ?? string.Empty,
                    label: label,
                    started: started,
                    lastTurn: lastTurn,
                    busy: Updating);
        }
        catch (Exception ex)
        {
            // Reached from a gesture: an export that cannot start must not take the app down,
            // and the flow reports its own failures to the caregiver.
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while exporting a conversation");
        }
    }

    /// <summary>The "Start a new conversation" action belongs to a live thread with something in
    /// it — never to history browsing, an empty window, or the middle of a send.</summary>
    private void UpdateNewConversationAction()
    {
        var onALiveThread = _mode == ChatViewMode.Thread && _turns.Count > 0 && !_isSending;
        NewConversationAction.IsVisible = onALiveThread;

        // Also needs a session to name: a thread whose first send failed has bubbles on screen
        // and nothing persisted behind them, and an export of it would be an empty document.
        ExportThreadAction.IsVisible = onALiveThread && _currentSessionId != Guid.Empty;
    }

    /// <summary>Puts the live thread back: its bubbles, its input bar, its subtitle. If the
    /// thread's own load had failed, returning here retries it instead of presenting the empty
    /// list the failure left behind.</summary>
    private void ShowThread()
    {
        _mode = ChatViewMode.Thread;
        ExitSessionSelection();
        ShowThreadSubtitle();
        TurnsList.ItemsSource = _turns;
        InputBar.IsVisible = true;
        BackToChatPanel.IsVisible = false;
        ContinuePanel.IsVisible = false;
        SessionsList.IsVisible = false;

        if (_threadLoadFailed)
        {
            UpdateNewConversationAction();
            _loadTask = LoadAsync();
            return;
        }

        SetState(loaded: true);
        SuggestionsPanel.IsVisible = _turns.Count == 0 && SuggestionsRow.Count > 0;
        UpdateNewConversationAction();
        ScrollToLatest(animate: false);
    }

    private void ApplyThread(MemberChatHistoryResponse? history)
    {
        _turns.Clear();
        _currentSessionId = history?.SessionId ?? Guid.Empty;
        _currentStartedOn = default;
        if (history is null)
            return;

        if (history.Turns.Count > 0)
        {
            _currentStartedOn = DateOnly.FromDateTime(
                history.Turns[0].CreatedAtUtc.ToLocalTime().DateTime);
        }

        foreach (var turn in history.Turns)
            _turns.Add(ChatTurnItem.FromHistory(turn, _memberFirstName));
    }

    private async Task ShowHistoryListAsync()
    {
        _mode = ChatViewMode.HistoryList;
        ExitSessionSelection();
        ShowSubtitle("Past conversations");
        SuggestionsPanel.IsVisible = false;
        InputBar.IsVisible = false;
        NewConversationAction.IsVisible = false;
        BackToChatPanel.IsVisible = true;
        ContinuePanel.IsVisible = false;
        SessionsList.IsVisible = false;
        SelectSessionsAction.IsVisible = false;

        var shownFromCache = _sessions.Count > 0;
        if (shownFromCache)
        {
            SetState();
            SessionsList.IsVisible = true;
            SelectSessionsAction.IsVisible = true;
        }
        else if (await _api.PeekMemberChatSessionsAsync(_memberId) is { } saved)
        {
            _sessions.Clear();
            foreach (var session in saved.Sessions)
                _sessions.Add(ChatSessionItem.From(session));
            shownFromCache = true;
            SetState();
            SessionsList.IsVisible = true;
            SelectSessionsAction.IsVisible = _sessions.Count > 0;
        }
        else
            SetState(loading: true);

        try
        {
            var response = await _api.GetMemberChatSessionsAsync(_memberId);

            // The caregiver may have tapped their way out while this was in flight — the mode
            // they moved to owns the panels now.
            if (_mode != ChatViewMode.HistoryList)
                return;

            _sessions.Clear();
            foreach (var session in response.Sessions)
                _sessions.Add(ChatSessionItem.From(session));

            SetState();
            SessionsList.IsVisible = true;
            // Nothing to select in an empty history, and a Select pill over an empty list is an
            // affordance for an act that cannot happen.
            SelectSessionsAction.IsVisible = _sessions.Count > 0;
        }
        catch (ApiException ex)
        {
            if (_mode != ChatViewMode.HistoryList)
                return;
            if (ex.IsNotFound)
            {
                _sessions.Clear();
                ErrorDetailLabel.Text = ex.Message;
                SetState(error: true);
                return;
            }

            if (KeepCachedHistory())
                return;
            ErrorDetailLabel.Text = ex.Message;
            SetState(error: true);
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while loading past conversations");
            if (_mode != ChatViewMode.HistoryList)
                return;
            if (KeepCachedHistory())
                return;
            ErrorDetailLabel.Text = "Something went wrong while showing this.";
            SetState(error: true);
        }

        bool KeepCachedHistory()
        {
            if (!shownFromCache && _sessions.Count == 0)
                return false;

            SetState();
            SessionsList.IsVisible = true;
            SelectSessionsAction.IsVisible = _sessions.Count > 0;
            return true;
        }
    }

    private async Task ShowPastSessionAsync(ChatSessionItem item)
    {
        _mode = ChatViewMode.PastSession;
        _viewedSessionId = item.SessionId;
        _viewedSession = item;
        ShowSubtitle(item.OpenedLabel);
        SessionsList.IsVisible = false;
        BackToChatPanel.IsVisible = false;
        SetState(loading: true);

        try
        {
            var history = await _api.GetMemberChatSessionAsync(_memberId, item.SessionId);
            if (_mode != ChatViewMode.PastSession)
                return;

            _pastTurns.Clear();
            foreach (var turn in history.Turns)
                _pastTurns.Add(ChatTurnItem.FromHistory(turn, _memberFirstName));

            TurnsList.ItemsSource = _pastTurns;
            SetState(loaded: true);
            ContinuePanel.IsVisible = true;
        }
        catch (ApiException ex)
        {
            if (_mode != ChatViewMode.PastSession)
                return;
            ErrorDetailLabel.Text = ex.Message;
            SetState(error: true);
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while opening a past conversation");
            if (_mode != ChatViewMode.PastSession)
                return;
            ErrorDetailLabel.Text = "Something went wrong while showing this.";
            SetState(error: true);
        }
    }

    private async void OnSendTapped(object? sender, EventArgs e)
    {
        _ = BounceSendIconAsync();
        var typed = MessageEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(typed))
            return;

        MessageEditor.Text = string.Empty;
        await SendAsync(typed);
    }

    /// <summary>
    /// Fetches the chips and renders them. Failure is silent by design: chips are an
    /// affordance, and a caregiver who never sees them can still type — surfacing an error over
    /// missing suggestions would be worse than their absence.
    /// </summary>
    private async Task LoadSuggestionsAsync()
    {
        var memberId = _memberId;
        try
        {
            if (await _api.PeekMemberChatSuggestionsAsync(memberId) is { Suggestions.Count: > 0 } saved
                && memberId == _memberId)
                ShowSuggestions(saved);

            var response = await _api.GetMemberChatSuggestionsAsync(memberId);
            // Chips for a member the caregiver has since switched away from are not theirs.
            if (memberId == _memberId)
                ShowSuggestions(response);
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while loading suggestions");
        }
    }

    private void ShowSuggestions(MemberChatSuggestionsResponse response)
    {
        // _isSending as well as the turn count: a send hides this panel before it appends the
        // caregiver's own bubble, so a reply to this request landing in that gap would find
        // an empty thread and put the chips back underneath a message already on its way.
        // The mode check is the same race one layer out — a caregiver already looking at
        // history must not get the thread's chips drawn under the sessions list.
        if (response.Suggestions.Count == 0 || _turns.Count > 0 || _isSending
            || _mode != ChatViewMode.Thread || _choosing)
            return;

        SuggestionsRow.Clear();
        foreach (var suggestion in response.Suggestions)
            SuggestionsRow.Add(BuildSuggestionChip(suggestion));

        SuggestionsPanel.IsVisible = _turns.Count == 0;
    }

    /// <summary>
    /// One tappable question pill. Built in code rather than XAML, following the same convention
    /// as <see cref="Controls.AlertFilterSheetPage"/>'s chips — whose pill language these
    /// deliberately match in radius and type — because those are built for the sheet's four fixed
    /// questions and cannot serve free-text labels without being generalised for one caller.
    /// </summary>
    private Border BuildSuggestionChip(string suggestion)
    {
        var chip = new Border
        {
            BackgroundColor = Microsoft.Maui.Controls.Application.Current?.Resources["White"] as Color ?? Colors.White,
            Stroke = (Microsoft.Maui.Controls.Application.Current?.Resources["PrimaryDark"] as Color ?? Colors.Blue)
                .WithAlpha(0.5f),
            StrokeThickness = 1,
            Padding = new Thickness(20, 9, 20, 9),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            Content = new Label
            {
                Text = suggestion,
                FontFamily = "QuicksandSemiBold",
                FontSize = 14,
                TextColor = Microsoft.Maui.Controls.Application.Current?.Resources["HeadingText"] as Color ?? Colors.Black,
                VerticalOptions = LayoutOptions.Center,
            },
        };

        // A Border with a recognizer is static text to TalkBack — the question is read out but
        // never offered as something to activate, which makes the whole affordance invisible to
        // exactly the caregivers a one-tap question helps most.
        SemanticProperties.SetDescription(chip, suggestion);
        SemanticProperties.SetHint(chip, "Double tap to ask this question");

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await SendAsync(suggestion);
        chip.GestureRecognizers.Add(tap);
        return chip;
    }

    /// <summary>
    /// Puts the newest bubble at the bottom of the view — where a conversation is read from.
    /// </summary>
    /// <remarks>
    /// Dispatched rather than called straight through: <c>CollectionView.ScrollTo</c>
    /// resolves an index against the layout as it stands, and the item being scrolled to has just
    /// been added, so on the frame it is called the row it names does not have a position yet.
    /// Posting it runs the scroll after that pass. Failures are swallowed for the same reason they
    /// are dispatched — a chat that scrolled a little late is a chat; one that threw out of a
    /// gesture handler is not.
    /// </remarks>
    private void ScrollToLatest(bool animate = true)
    {
        if (_turns.Count == 0)
            return;

        Dispatcher.Dispatch(() =>
        {
            // Re-read the count here, not from the closure: a reload can clear the collection
            // between the post and the frame that runs it, and the index this was going to name
            // would then be one past a list that no longer has it.
            var last = _turns.Count - 1;
            if (last < 0)
                return;

            try
            {
                TurnsList.ScrollTo(last, position: ScrollToPosition.End, animate: animate);
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
            {
                // The narrow case only: a list still mid-rebuild refuses the index. Anything else
                // is a bug and is left to surface — a scroll is not worth hiding a null reference
                // behind. The next append scrolls again regardless.
            }
        });
    }

    /// <summary>A small, self-contained press animation — deliberately not awaited by the send
    /// itself, so a slow reply never holds the bounce open waiting for it.</summary>
    private async Task BounceSendIconAsync()
    {
        await SendIcon.ScaleToAsync(0.75, 90, Easing.CubicOut);
        await SendIcon.ScaleToAsync(1, 180, Easing.SpringOut);
    }

    private async Task LoadAsync()
    {
        // A reload while a send is in flight would rebuild the list out from under the turns
        // the send just appended — and the send's own completion is the fresher state anyway.
        if (_isLoading || _isSending || _memberId == Guid.Empty)
            return;
        _isLoading = true;

        // The member this load is for. ChooseAsync waits for a load in flight before switching,
        // so this only guards the stretch between that wait and the switch — cheap to be sure.
        var memberId = _memberId;

        var shownFromCache = false;
        if (_turns.Count == 0)
        {
            if (await _api.PeekCurrentMemberChatSessionAsync(memberId) is { } saved && memberId == _memberId)
            {
                ApplyThread(saved);
                SetState(loaded: true);
                shownFromCache = true;
            }
            else
                SetState(loading: true);
        }

        try
        {
            var history = await _api.GetCurrentMemberChatSessionAsync(memberId);
            if (memberId != _memberId || _choosing)
                return;
            ApplyThread(history);

            _threadLoadFailed = false;

            // The caregiver may have opened history while this was in flight — keep the data,
            // leave the panels to the mode that owns them now.
            if (_mode != ChatViewMode.Thread)
                return;

            SetState(loaded: true);
            UpdateNewConversationAction();

            // Only on an empty conversation — a resumed session already has its own thread to
            // follow. Deliberately not awaited: the chips are an affordance, and the sheet must
            // not wait on them to become usable.
            if (_turns.Count == 0)
            {
                PlayGreeting();
                _ = LoadSuggestionsAsync();
            }
            else
            {
                SuggestionsPanel.IsVisible = false;
                // Opened at the top, a resumed conversation shows its oldest turn — which on a
                // thread of any length is a screen of things the caregiver has already read, and
                // several swipes away from the answer they came back for. Not animated: this is
                // where the sheet opens, not somewhere it travels to.
                ScrollToLatest(animate: false);
            }
        }
        catch (ApiException ex)
        {
            if (ex.IsNotFound)
            {
                ApplyThread(null);
                _threadLoadFailed = true;
                if (_mode == ChatViewMode.Thread)
                {
                    ErrorDetailLabel.Text = ex.Message;
                    SetState(error: true);
                }

                return;
            }

            _threadLoadFailed = !shownFromCache && _turns.Count == 0;
            if (!shownFromCache && _turns.Count == 0 && _mode == ChatViewMode.Thread)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(error: true);
            }
        }
        catch (Exception ex)
        {
            // Same async-void-has-no-observer hole MedicalInformationPage documents on its own
            // OnAppearing/pull handlers — without this the page never leaves its skeleton.
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while loading");
            _threadLoadFailed = !shownFromCache && _turns.Count == 0;
            if (!shownFromCache && _turns.Count == 0 && _mode == ChatViewMode.Thread)
            {
                ErrorDetailLabel.Text = "Something went wrong while showing this.";
                SetState(error: true);
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <param name="message">
    /// What to send. Passed in rather than read from the editor here so a suggestion chip can
    /// send its own question without round-tripping through the input field.
    /// </param>
    private async Task SendAsync(string message)
    {
        if (_isSending)
            return;

        if (string.IsNullOrWhiteSpace(message))
            return;

        _isSending = true;
        SendButton.IsEnabled = false;

        // Never a first message to the assistant without the notice: normally it was shown when
        // the sheet opened, and this returns at once. If it could not be shown, the message is
        // not sent — it goes back in the field, where one more tap tries the notice again.
        if (!await EnsureAiNoticeSeenAsync())
        {
            if (string.IsNullOrWhiteSpace(MessageEditor.Text))
                MessageEditor.Text = message;
            _isSending = false;
            SendButton.IsEnabled = true;
            return;
        }

        // The chips are a first-message affordance: once the conversation has started, what to
        // ask next comes from the reply, not from a generic list. The new-conversation action
        // steps aside too — ending a conversation mid-send would race the reply.
        SuggestionsPanel.IsVisible = false;
        UpdateNewConversationAction();

        // Let an in-flight history load land first — its rebuild clears the list, and a turn
        // appended before that Clear() would silently vanish. LoadAsync never faults (it
        // handles its own failures), so awaiting it here cannot throw.
        if (_loadTask is { IsCompleted: false } pendingLoad)
            await pendingLoad;

        var userTurn = ChatTurnItem.FromUserMessage(message);
        _turns.Add(userTurn);

        // The list holding these bubbles must actually be on screen. If the history load failed
        // (or hasn't finished), the page is still showing its error or skeleton panel — and a
        // message appended behind either of those simply vanishes, which is exactly what a
        // caregiver reported. Sending is proof the conversation is where they are; show it.
        SetState(loaded: true);

        // The reply is a chain of model calls and legitimately takes a while — an empty slot
        // for that long reads as a swallowed message. The pending panel says it's being worked
        // on: the bot mark starts breathing immediately, and each step the server reports
        // replaces the line beside it as that step starts, so the wait says what is actually
        // happening. Progress<T> posts each report back to this (UI) thread.
        var pending = new PendingProgress(this);
        pending.Start();
        ScrollToLatest();
        var steps = new Progress<MemberChatStep>(pending.OnStep);
        var waitingLines = new Progress<IReadOnlyList<string>>(pending.OnWaitingLines);

        // A reply the server goes on to check arrives first as a draft: shown at once, so the
        // caregiver reads it while the check runs, with the pending line beneath it still saying
        // what is happening. The saved reply replaces it only when the check changed it.
        MemberChatMessageResponse? draft = null;
        ChatTurnItem? draftItem = null;
        var settled = false;
        var drafts = new Progress<MemberChatMessageResponse>(d =>
        {
            // Progress posts to this thread; a post that somehow lands after the send has settled
            // would add a second bubble beside the answer it drafted.
            if (settled)
                return;
            draft = d;
            pending.OnDraft();
            draftItem = ChatTurnItem.FromReply(d, _memberFirstName);
            _turns.Add(draftItem);
            ScrollToLatest();
        });

        try
        {
            var response = await _api.StreamMemberChatMessageAsync(
                _memberId, new MemberChatMessageRequest { Message = message }, steps, drafts, waitingLines);
            settled = true;
            // The first send of a window is what creates the session, so this is where the
            // thread learns which conversation it is — the export action needs it named.
            _currentSessionId = response.SessionId;
            if (_currentStartedOn == default)
                _currentStartedOn = DateOnly.FromDateTime(DateTime.Now);

            if (draftItem is null)
            {
                _turns.Add(ChatTurnItem.FromReply(response, _memberFirstName));
            }
            else if (!ReferenceEquals(response, draft))
            {
                // The client hands back the draft itself when nothing replaced it, so anything
                // else is an answer.updated — new words, or the same words over new charts.
                // In place, and labelled: the caregiver may already have read the first version,
                // and a bubble that silently changed under them would read as a glitch.
                var index = _turns.IndexOf(draftItem);
                var updated = ChatTurnItem.FromReply(response, _memberFirstName, updated: true);
                if (index >= 0)
                    _turns[index] = updated;
                else
                    _turns.Add(updated);
            }
        }
        catch (ApiException ex)
        {
            settled = true;
            // A draft already on screen was never saved — the send failed after it — so it gives
            // way to the error rather than standing as an answer the history will not have.
            if (draftItem is not null)
                _turns.Remove(draftItem);
            // The question stays in the list — retyping it would be worse than seeing why it
            // didn't get an answer. The reply slot carries the error instead of a made-up answer.
            _turns.Add(ChatTurnItem.FromError(ex.Message));
        }
        catch (Exception ex)
        {
            settled = true;
            if (draftItem is not null)
                _turns.Remove(draftItem);
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while sending a message");
            _turns.Add(ChatTurnItem.FromError("Something went wrong sending that — try again."));
        }
        finally
        {
            ScrollToLatest();
            pending.Stop();
            _isSending = false;
            SendButton.IsEnabled = true;
            UpdateNewConversationAction();
        }
    }

    /// <summary>What the pending bubble says between the tap and the server's first step — the
    /// few seconds the pre-check takes, before the stream has anything to report.</summary>
    private const string SendingLine = "Reading your question…";

    /// <summary>
    /// The pending bubble for one send: the step the server is on, a bar of how far along it is
    /// once the route is known, and — when a step has been on screen for
    /// <see cref="WaitingLineDelay"/> — the lines written for this question, rotating beneath it
    /// every <see cref="WaitingLineRotation"/>. All of it on the UI thread: the reports arrive
    /// through <see cref="Progress{T}"/>, and the rotation is a dispatcher timer.
    /// </summary>
    private sealed class PendingProgress(MemberChatPage page)
    {
        private readonly IDispatcherTimer _timer = page.Dispatcher.CreateTimer();
        private DateTime _stepShownAt;
        private DateTime _lineShownAt;
        private IReadOnlyList<string>? _lines;
        private int _nextLine;
        private int _barSegments;

        /// <summary>A draft reply is on screen: the question is answered, and lines about what
        /// is being looked at would now describe work that is done.</summary>
        private bool _drafted;

        public void Start()
        {
            page.PendingTextLabel.Text = SendingLine;
            page.PendingDetailLabel.IsVisible = false;
            page.PendingProgressRow.IsVisible = false;
            page.PendingPanel.IsVisible = true;
            _stepShownAt = DateTime.UtcNow;

            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += OnTick;
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            page.PendingPanel.IsVisible = false;
            page.PendingDetailLabel.IsVisible = false;
            page.PendingProgressRow.IsVisible = false;
        }

        public void OnStep(MemberChatStep step)
        {
            page.PendingTextLabel.Text = step.Text;
            // Each step starts clean and earns its lines again: a line left over from the last
            // step would sit under a step it was not written for.
            page.PendingDetailLabel.IsVisible = false;
            _stepShownAt = DateTime.UtcNow;
            ShowProgress(step.Index, step.Total);
        }

        public void OnWaitingLines(IReadOnlyList<string> lines)
        {
            _lines = lines;
            _nextLine = 0;
        }

        public void OnDraft()
        {
            _drafted = true;
            page.PendingDetailLabel.IsVisible = false;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_drafted || _lines is not { Count: > 0 } lines)
                return;

            var now = DateTime.UtcNow;
            if (now - _stepShownAt < WaitingLineDelay)
                return;
            if (page.PendingDetailLabel.IsVisible && now - _lineShownAt < WaitingLineRotation)
                return;

            page.PendingDetailLabel.Text = lines[_nextLine];
            page.PendingDetailLabel.IsVisible = true;
            _nextLine = (_nextLine + 1) % lines.Count;
            _lineShownAt = now;
        }

        /// <summary>
        /// The bar and "Step n of N". Hidden until the server says how many steps there are —
        /// the first step arrives before the route that decides it — and on any step it did not
        /// number, so an older server leaves the bubble exactly as it was.
        /// </summary>
        private void ShowProgress(int? index, int? total)
        {
            if (index is not { } current || total is not { } count || count < 1)
            {
                page.PendingProgressRow.IsVisible = false;
                return;
            }

            current = Math.Clamp(current, 1, count);
            var bar = page.PendingStepBar;
            if (_barSegments != count)
            {
                bar.Children.Clear();
                bar.ColumnDefinitions.Clear();
                for (var i = 0; i < count; i++)
                {
                    bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    var segment = new BoxView { CornerRadius = 2, HeightRequest = 4 };
                    Grid.SetColumn(segment, i);
                    bar.Children.Add(segment);
                }

                _barSegments = count;
            }

            var done = ResourceColor("Primary");
            var todo = ResourceColor("ProgressTrack");
            for (var i = 0; i < bar.Children.Count; i++)
                ((BoxView)bar.Children[i]).Color = i < current ? done : todo;

            page.PendingStepLabel.Text = $"Step {current} of {count}";
            page.PendingProgressRow.IsVisible = true;
        }

        private static Color ResourceColor(string key) =>
            (Color)Microsoft.Maui.Controls.Application.Current!.Resources[key];
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        Refresher.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
    }
}

/// <summary>
/// One chat bubble, with every display value pre-computed at construction — this app's
/// established convention (see <c>MetricTrend</c>) for keeping XAML free of converters.
/// </summary>
public sealed class ChatTurnItem
{
    public required string Content { get; init; }
    public required bool IsUser { get; init; }
    public required string RoleLabel { get; init; }
    public required bool ShowRoleLabel { get; init; }

    /// <summary>
    /// When this turn was sent or generated, in the reader's local time — a conversation a
    /// caregiver returns to hours later needs to say whether "steady today" was this morning or
    /// last night. Empty on an error bubble, which describes a failure rather than a moment in
    /// the thread.
    /// </summary>
    public string Timestamp { get; init; } = string.Empty;
    public bool HasTimestamp => Timestamp.Length > 0;

    /// <summary>
    /// The time in 12-hour form, with the day added whenever the turn is not from today. A bare
    /// "11:58 pm" on a thread opened the next morning reads as last night at best and as this
    /// morning at worst — and a session that runs past midnight, or one reopened later, puts
    /// exactly that line on screen. Today's turns stay time-only: on the common path the date
    /// would repeat on every bubble and say nothing.
    /// </summary>
    private static string FormatTimestamp(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        // Invariant for the clock: the designator is asked for explicitly, and a 24-hour culture
        // would silently drop it. The date part stays culture-aware, where month names belong.
        var time = local.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();

        var day = DateOnly.FromDateTime(local.DateTime);
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (day == today)
            return time;

        return day == today.AddDays(-1)
            ? $"Yesterday {time}"
            : $"{local.ToString("MMM d", CultureInfo.CurrentCulture)} {time}";
    }
    public required Color TextColor { get; init; }
    public required Color BubbleBackground { get; init; }
    public required LayoutOptions RowAlignment { get; init; }

    /// <summary>Elevation for the bubble. Only the caregiver's own bubbles carry one — the
    /// bot's replies (and errors, and resumed history) sit flat, so the conversation reads as
    /// one surface rather than a stack of floating cards.</summary>
    public Shadow? BubbleShadow { get; init; }
    public string ChartSummary { get; init; } = string.Empty;
    public bool HasChartSummary => !string.IsNullOrEmpty(ChartSummary);

    /// <summary>
    /// The reply's trailing "Reference:" / "References:" line, split out of <see cref="Content"/>
    /// and rebuilt with each cited authority as a tappable link to where the guidance is actually
    /// published — see <see cref="SplitReference"/>. Null when the reply quotes nothing.
    /// </summary>
    public FormattedString? ReferenceText { get; init; }
    public bool HasReference => ReferenceText is not null;

    /// <summary>The reply's supporting series, pre-shaped for drawing — see
    /// <see cref="ChatChartItem.From"/>. Empty for user turns and errors; a resumed or refreshed
    /// reply draws the series stored with its turn.</summary>
    public IReadOnlyList<ChatChartItem> Charts { get; init; } = [];
    public bool HasCharts => Charts.Count > 0;

    /// <summary>
    /// The chart carousel's height. A carousel cannot size itself to its items, so it is fixed to
    /// what <see cref="ChatSeriesChart"/> stacks — and one row taller when a chart in it carries
    /// the awake-night key on a line of its own, which the fixed height used to clip in half.
    /// </summary>
    public double ChartsHeight => Charts.Any(c => c.HasAwakeNight) ? 191 : 175;

    /// <summary>
    /// Whether the chart carousel wraps around and shows its pager row (the dots with an arrow
    /// either side) — only when there is more than one chart to move between. A single-chart
    /// reply shows no pager at all: a lone dot would announce a carousel that cannot be swiped,
    /// and an arrow with nowhere to go is a control that lies.
    /// </summary>
    /// <remarks>
    /// There is deliberately no peek inset: a sliver of the neighbouring chart bled into this
    /// one's own axis figures and legend at bubble width, which read as one broken chart rather
    /// than two adjacent ones. The arrows are the "there are more" affordance instead.
    /// </remarks>
    public bool ChartsLoop => Charts.Count > 1;

    /// <summary>
    /// How wide this bubble may run. A charted reply takes the full sheet — the plot is the part
    /// meant to be read closely, and it was the narrowest thing on screen at the conversational
    /// measure. Everything else keeps that measure, which is what makes prose readable.
    /// </summary>
    public double BubbleMaxWidth => HasCharts ? 460 : 300;

    /// <summary>
    /// How the bubble measures against the row. <see cref="LayoutOptions.Start"/> and
    /// <see cref="LayoutOptions.End"/> size to content, which is right for prose and wrong for a
    /// chart: a <c>MaximumWidthRequest</c> only caps a measured size, it never grows one, so a
    /// charted bubble left on Start measured to its widest label and drew the plot narrower than
    /// the text above it. Charted replies therefore measure Fill and are capped by
    /// <see cref="BubbleMaxWidth"/>; they are the bot's, so they still read as left-aligned.
    /// </summary>
    public LayoutOptions BubbleAlignment => HasCharts ? LayoutOptions.Fill : RowAlignment;

    /// <param name="sentAt">When the turn happened. Defaults to now for a message being sent
    /// this moment; history passes the stored time.</param>
    public static ChatTurnItem FromUserMessage(string content, DateTimeOffset? sentAt = null) => new()
    {
        Content = content,
        IsUser = true,
        RoleLabel = "You",
        ShowRoleLabel = false,
        Timestamp = FormatTimestamp(sentAt ?? DateTimeOffset.UtcNow),
        TextColor = Colors.White,
        BubbleBackground = Microsoft.Maui.Controls.Application.Current?.Resources["Primary"] as Color ?? Colors.Blue,
        RowAlignment = LayoutOptions.End,
        // No shadow. The caregiver's own messages already stand off the ground by being solid
        // Primary against it, and a raised bubble reads as a card with something behind it —
        // which a sent message has not got. The bot's replies were always flat; this makes the
        // pair one conversation rather than two kinds of object.
        BubbleShadow = null,
    };

    /// <summary>
    /// Splits series into the ones worth drawing and the ones that still need saying in text.
    /// Shared by the live reply and the stored one so a refreshed conversation cannot render its
    /// charts by a different rule than the answer did when it arrived.
    /// </summary>
    /// <remarks>
    /// A single reading is dropped from both. It has no trend to plot, and the text stand-in it
    /// used to fall into was written before the charts existed — so an answer about this
    /// afternoon appended "Steps: 774 · Resting heart rate: 72 · Sleep: 6h 12m" underneath a
    /// sentence that had just said all three. Evidence is worth showing when it shows something:
    /// a clinician puts a chart in front of you to make a trend visible, and states a single
    /// reading in the sentence where it belongs. The reply already states it.
    /// <para>
    /// The summary survives for the case it was actually needed for — two or more readings that
    /// still would not chart, which today means a span so wide the plot would be mostly gap. That
    /// is data the prose has no room for and a chart cannot carry.
    /// </para>
    /// </remarks>
    private static (List<ChatChartItem> Drawable, List<ChartSeries> Summarised) SplitCharts(
        IReadOnlyList<ChartSeries> charts)
    {
        var drawable = new List<ChatChartItem>();
        var summarised = new List<ChartSeries>();
        foreach (var series in charts)
        {
            if (ChatChartItem.From(series) is { } item)
                drawable.Add(item);
            else if (series.Points.Count > 1)
                summarised.Add(series);
        }

        return (drawable, summarised);
    }

    /// <param name="updated">The saved reply replacing a draft the answer check changed —
    /// labelled so a caregiver who read the first version sees why the words moved.</param>
    public static ChatTurnItem FromReply(MemberChatMessageResponse response, string? memberFirstName, bool updated = false)
    {
        var (drawable, summarised) = SplitCharts(response.Charts);
        var (body, reference) = SplitReference(response.Reply);

        return new ChatTurnItem
        {
            Content = body,
            ReferenceText = reference,
            IsUser = false,
            RoleLabel = updated
                ? "Updated answer"
                : memberFirstName is { Length: > 0 } name ? $"About {name}" : "Reply",
            ShowRoleLabel = true,
            TextColor = Microsoft.Maui.Controls.Application.Current?.Resources["HeadingText"] as Color ?? Colors.Black,
            BubbleBackground = Microsoft.Maui.Controls.Application.Current?.Resources["White"] as Color ?? Colors.White,
            RowAlignment = LayoutOptions.Start,
            Charts = drawable,
            ChartSummary = Summarize(summarised),
            Timestamp = FormatTimestamp(response.GeneratedAt),
        };
    }

    public static ChatTurnItem FromError(string message) => new()
    {
        Content = message,
        IsUser = false,
        RoleLabel = "Couldn't answer",
        ShowRoleLabel = true,
        TextColor = Microsoft.Maui.Controls.Application.Current?.Resources["HeadingText"] as Color ?? Colors.Black,
        BubbleBackground = Microsoft.Maui.Controls.Application.Current?.Resources["White"] as Color ?? Colors.White,
        RowAlignment = LayoutOptions.Start,
    };

    public static ChatTurnItem FromHistory(MemberChatTurnResponse turn, string? memberFirstName)
    {
        if (turn.Role == "User")
            return FromUserMessage(turn.Content, turn.CreatedAtUtc);

        var (drawable, summarised) = SplitCharts(turn.Charts);
        var (body, reference) = SplitReference(turn.Content);

        return new ChatTurnItem
        {
            Content = body,
            ReferenceText = reference,
            IsUser = false,
            RoleLabel = memberFirstName is { Length: > 0 } name ? $"About {name}" : "Reply",
            ShowRoleLabel = true,
            TextColor = Microsoft.Maui.Controls.Application.Current?.Resources["HeadingText"] as Color ?? Colors.Black,
            BubbleBackground = Microsoft.Maui.Controls.Application.Current?.Resources["White"] as Color ?? Colors.White,
            RowAlignment = LayoutOptions.Start,
            Charts = drawable,
            ChartSummary = Summarize(summarised),
            Timestamp = FormatTimestamp(turn.CreatedAtUtc),
        };
    }

    /// <summary>
    /// Splits a reply into its prose and the trailing citation block the advise and inference
    /// rungs close with ("Reference: …" / "References: …; …."), so the citations can be rendered
    /// as their own quiet line with each authority tappable. Anything that is not exactly that
    /// trailing block — a "Reference" mid-prose, a truncated line — stays in the prose untouched:
    /// losing a caregiver's answer to a parse is the one failure this split must not have.
    /// </summary>
    private static (string Body, FormattedString? Reference) SplitReference(string content)
    {
        var idx = content.LastIndexOf("\n\nReference", StringComparison.Ordinal);
        if (idx < 0)
            return (content, null);

        var block = content[(idx + 2)..];
        var colon = block.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0 || block.Contains('\n')
            || (block[..colon] != "Reference" && block[..colon] != "References"))
            return (content, null);

        // Only a complete block is split: the servers close every citation line with exactly one
        // period, so a tail without one is a truncated line (the reply cap ends in an ellipsis)
        // and stays in the prose as stored. Exactly one period comes off, and BuildReference puts
        // exactly one back — which is what keeps the split lossless.
        var blob = block[(colon + 2)..].TrimEnd();
        if (!blob.EndsWith('.'))
            return (content, null);

        var citations = blob[..^1].Split("; ", StringSplitOptions.RemoveEmptyEntries);
        if (citations.Length == 0)
            return (content, null);

        return (content[..idx].TrimEnd(), BuildReference(block[..(colon + 2)], citations));
    }

    /// <summary>
    /// The citation block as spans: the label and citation text quiet, each authority whose
    /// guidance has a published page (<see cref="CitationLinks"/>) underlined, in the link colour,
    /// and tappable — opening the source in the browser. A citation the closed sets don't carry a
    /// URL for renders as plain text: no link beats a link that leads nowhere.
    /// </summary>
    private static FormattedString BuildReference(string label, IReadOnlyList<string> citations)
    {
        var quiet = Microsoft.Maui.Controls.Application.Current?.Resources["MutedText"] as Color
            ?? Colors.Gray;
        var linked = Microsoft.Maui.Controls.Application.Current?.Resources["Primary"] as Color
            ?? Colors.Blue;

        var reference = new FormattedString();
        reference.Spans.Add(new Span { Text = label, TextColor = quiet });

        for (var i = 0; i < citations.Count; i++)
        {
            if (i > 0)
                reference.Spans.Add(new Span { Text = "; ", TextColor = quiet });

            var citation = citations[i];
            var url = CitationLinks.UrlFor(citation);
            if (url is null)
            {
                reference.Spans.Add(new Span { Text = citation, TextColor = quiet });
                continue;
            }

            // The authority name is the link — the part a caregiver recognises — and the figures
            // after the dash stay plain, so the line doesn't read as one long underline.
            var dash = citation.IndexOf(" — ", StringComparison.Ordinal);
            var authority = dash > 0 ? citation[..dash] : citation;

            var authoritySpan = new Span
            {
                Text = authority,
                TextColor = linked,
                TextDecorations = TextDecorations.Underline,
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                try
                {
                    await Launcher.Default.OpenAsync(new Uri(url));
                }
                catch (Exception ex)
                {
                    // A device with no handler for the URL, or a launcher that refuses — the
                    // citation is still on screen, which is what it was before it was a link.
                    ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while opening a reference");
                }
            };
            authoritySpan.GestureRecognizers.Add(tap);
            reference.Spans.Add(authoritySpan);

            if (dash > 0)
                reference.Spans.Add(new Span { Text = citation[dash..], TextColor = quiet });
        }

        reference.Spans.Add(new Span { Text = ".", TextColor = quiet });
        return reference;
    }

    /// <summary>
    /// First-to-last for the series <see cref="SplitCharts"/> could neither draw nor drop: two or
    /// more readings spanning too wide a window to plot.
    /// </summary>
    /// <remarks>
    /// Single-reading series no longer reach here — they are dropped, because one reading has no
    /// stretch to describe and the reply states it in prose. So there is no one-point branch: an
    /// arrow always sits between two different readings, which is the only shape that ever meant
    /// anything. Values are spelled the way the charts spell them, so a night's sleep does not
    /// read as "372" beneath a bubble that just called it six hours — nor an awake night as "0m".
    /// </remarks>
    private static string Summarize(IReadOnlyList<ChartSeries> charts)
    {
        var parts = charts
            .Where(c => c.Points.Count > 1)
            .Select(c => $"{c.Metric}: {ChatMetricFormat.Point(c.Metric, c.Points[0])} → "
                + $"{ChatMetricFormat.Point(c.Metric, c.Points[^1])}");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// One row of the history list, display values pre-computed at construction — the same
/// converter-free convention as <see cref="ChatTurnItem"/>.
/// </summary>
public sealed class ChatSessionItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private bool _isSelecting;
    private bool _isSelected;

    /// <summary>
    /// Whether the history list is in selection mode — set on every row together, because the
    /// mode belongs to the list and the row only wears it: the checkmark ring appears, the
    /// chevron steps aside, and a tap picks instead of opening. Leaving the mode drops any
    /// selection with it, so a cancelled pick can never resurface later as a surprise delete.
    /// </summary>
    public bool IsSelecting
    {
        get => _isSelecting;
        set
        {
            if (_isSelecting == value)
                return;
            _isSelecting = value;
            if (!value)
                IsSelected = false;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelecting)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowChevron)));
        }
    }

    /// <summary>Whether this conversation is picked for deletion. Meaningful only while
    /// <see cref="IsSelecting"/>, which clears it on the way out.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>The open-me chevron, hidden while selecting — a tap no longer opens, and the
    /// glyph would promise exactly that.</summary>
    public bool ShowChevron => !_isSelecting;

    public required Guid SessionId { get; init; }

    /// <summary>The local days the conversation spans — what an export of it is dated by, and
    /// what the caregiver confirms. Local, not UTC: the row above says "Started yesterday", and a
    /// document dated the day before that would look like a different conversation.</summary>
    public required DateOnly StartedOn { get; init; }

    public required DateOnly LastTurnOn { get; init; }

    /// <summary>What names the row: the conversation's generated theme, or — until the theming
    /// job has visited it — the caregiver's opening question.</summary>
    public required string Title { get; init; }

    /// <summary>When the conversation began, to the minute — "Started yesterday · 2:14 pm".
    /// The start is the moment a caregiver relives a conversation from, so it is the mark the
    /// list carries.</summary>
    public required string Meta { get; init; }

    /// <summary>The header subtitle while this conversation is open — "From yesterday",
    /// "From Aug 21" — so the sheet says whose words the caregiver is rereading.</summary>
    public required string OpenedLabel { get; init; }

    public static ChatSessionItem From(MemberChatSessionSummaryResponse session)
    {
        var local = session.StartedAtUtc.ToLocalTime();
        var day = DateOnly.FromDateTime(local.DateTime);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var dayLabel = day == today ? "today"
            : day == today.AddDays(-1) ? "yesterday"
            : local.ToString("MMM d", CultureInfo.CurrentCulture);
        // Invariant for the clock, culture-aware for the date — same split, same reason, as
        // ChatTurnItem.FormatTimestamp.
        var time = local.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();

        return new ChatSessionItem
        {
            SessionId = session.SessionId,
            StartedOn = day,
            LastTurnOn = DateOnly.FromDateTime(session.LastTurnAtUtc.ToLocalTime().DateTime),
            // Theme first; opening question until one exists. A row whose question also
            // decrypts to nothing (written before encryption, or under a rotated key) still
            // gets a nameable label rather than a blank one.
            Title = !string.IsNullOrWhiteSpace(session.Theme) ? session.Theme
                : !string.IsNullOrWhiteSpace(session.FirstQuestion) ? session.FirstQuestion
                : "A conversation",
            Meta = $"Started {dayLabel} · {time}",
            OpenedLabel = day == today ? "From earlier today"
                : day == today.AddDays(-1) ? "From yesterday"
                : $"From {dayLabel}",
        };
    }
}
