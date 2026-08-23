using System.Collections.ObjectModel;
using System.Globalization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
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

    private readonly Guid _memberId;
    private readonly string? _memberFirstName;
    private readonly string _threadSubtitle;
    private ChatViewMode _mode = ChatViewMode.Thread;

    /// <summary>The session the thread is (or would be) continuing — how the history list knows
    /// which of its rows is this conversation rather than a past one.</summary>
    private Guid? _currentSessionId;

    private bool _isLoading;
    private bool _isSending;

    /// <summary>The last thread load failed — so a return from history retries it rather than
    /// presenting the empty list the failure left behind as a conversation.</summary>
    private bool _threadLoadFailed;

    /// <summary>The in-flight history load, if any — a send awaits it before appending, so the
    /// load's rebuild of the list cannot wipe turns added after it started. Never faults:
    /// <see cref="LoadAsync"/> handles its own failures.</summary>
    private Task? _loadTask;

    public MemberChatPage(ICardiTrackApiClient api, Guid memberId, string? memberFirstName)
    {
        InitializeComponent();
        _api = api;
        _memberId = memberId;
        _memberFirstName = memberFirstName;
        TurnsList.ItemsSource = _turns;
        SessionsList.ItemsSource = _sessions;

        if (!string.IsNullOrWhiteSpace(memberFirstName))
            SubtitleLabel.Text = $"What would you like to know about {memberFirstName}?";
        _threadSubtitle = SubtitleLabel.Text;

        // No OnAppearing on a ContentView — the host adds this to its tree only at the moment
        // it's shown (see MemberChatLauncher), so construction time is the right time to load.
        _loadTask = LoadAsync();
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

        // The row that is this conversation opens as the conversation — live, input and all —
        // not as a second, read-only copy of itself.
        if (item.IsCurrent)
        {
            ShowThread();
            return;
        }

        await ShowPastSessionAsync(item);
    }

    /// <summary>Puts the live thread back: its bubbles, its input bar, its subtitle. If the
    /// thread's own load had failed, returning here retries it instead of presenting the empty
    /// list the failure left behind.</summary>
    private void ShowThread()
    {
        _mode = ChatViewMode.Thread;
        SubtitleLabel.Text = _threadSubtitle;
        TurnsList.ItemsSource = _turns;
        InputBar.IsVisible = true;
        BackToChatPanel.IsVisible = false;
        SessionsList.IsVisible = false;

        if (_threadLoadFailed)
        {
            _loadTask = LoadAsync();
            return;
        }

        SetState(loaded: true);
        SuggestionsPanel.IsVisible = _turns.Count == 0 && SuggestionsRow.Count > 0;
        ScrollToLatest(animate: false);
    }

    private async Task ShowHistoryListAsync()
    {
        _mode = ChatViewMode.HistoryList;
        SubtitleLabel.Text = "Past conversations";
        SuggestionsPanel.IsVisible = false;
        InputBar.IsVisible = false;
        BackToChatPanel.IsVisible = true;
        SessionsList.IsVisible = false;
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
                _sessions.Add(ChatSessionItem.From(session, session.SessionId == _currentSessionId));

            SetState();
            SessionsList.IsVisible = true;
        }
        catch (ApiException ex)
        {
            if (_mode != ChatViewMode.HistoryList)
                return;
            ErrorDetailLabel.Text = ex.Message;
            SetState(error: true);
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while loading past conversations");
            if (_mode != ChatViewMode.HistoryList)
                return;
            ErrorDetailLabel.Text = "Something went wrong while showing this.";
            SetState(error: true);
        }
    }

    private async Task ShowPastSessionAsync(ChatSessionItem item)
    {
        _mode = ChatViewMode.PastSession;
        SubtitleLabel.Text = item.OpenedLabel;
        SessionsList.IsVisible = false;
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
        try
        {
            var response = await _api.GetMemberChatSuggestionsAsync(_memberId);

            // _isSending as well as the turn count: a send hides this panel before it appends the
            // caregiver's own bubble, so a reply to this request landing in that gap would find
            // an empty thread and put the chips back underneath a message already on its way.
            // The mode check is the same race one layer out — a caregiver already looking at
            // history must not get the thread's chips drawn under the sessions list.
            if (response.Suggestions.Count == 0 || _turns.Count > 0 || _isSending
                || _mode != ChatViewMode.Thread)
                return;

            SuggestionsRow.Clear();
            foreach (var suggestion in response.Suggestions)
                SuggestionsRow.Add(BuildSuggestionChip(suggestion));

            SuggestionsPanel.IsVisible = _turns.Count == 0;
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while loading suggestions");
        }
    }

    /// <summary>
    /// One tappable question pill. Built in code rather than XAML, following the same convention
    /// as <see cref="Controls.FilterChipBar"/> — whose own pills these deliberately match in
    /// padding, radius and type — because that control is enum-typed to AlertFilter and cannot
    /// serve free-text labels without being generalised for one caller.
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
        if (_isLoading || _isSending)
            return;
        _isLoading = true;

        if (_turns.Count == 0)
            SetState(loading: true);

        try
        {
            var history = await _api.GetCurrentMemberChatSessionAsync(_memberId);
            _turns.Clear();
            if (history is not null)
            {
                foreach (var turn in history.Turns)
                    _turns.Add(ChatTurnItem.FromHistory(turn, _memberFirstName));
            }

            _currentSessionId = history?.SessionId;
            _threadLoadFailed = false;

            // The caregiver may have opened history while this was in flight — keep the data,
            // leave the panels to the mode that owns them now.
            if (_mode != ChatViewMode.Thread)
                return;

            SetState(loaded: true);

            // Only on an empty conversation — a resumed session already has its own thread to
            // follow. Deliberately not awaited: the chips are an affordance, and the sheet must
            // not wait on them to become usable.
            if (_turns.Count == 0)
            {
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
            _threadLoadFailed = _turns.Count == 0;
            if (_turns.Count == 0 && _mode == ChatViewMode.Thread)
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
            _threadLoadFailed = _turns.Count == 0;
            if (_turns.Count == 0 && _mode == ChatViewMode.Thread)
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
        // The chips are a first-message affordance: once the conversation has started, what to
        // ask next comes from the reply, not from a generic list.
        SuggestionsPanel.IsVisible = false;

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
        // on: the bot mark starts breathing immediately, and the waiting copy the cycler fetches
        // fills in beside it when it lands.
        PendingTextLabel.Text = string.Empty;
        PendingPanel.IsVisible = true;
        ScrollToLatest();
        var waitingCts = new CancellationTokenSource();
        _ = CycleWaitingLinesAsync(message, waitingCts.Token);

        try
        {
            var response = await _api.SendMemberChatMessageAsync(
                _memberId, new MemberChatMessageRequest { Message = message });
            // The send may have started a fresh session (first message, or the active window
            // lapsed) — the history list tells its rows apart by this id.
            _currentSessionId = response.SessionId;
            _turns.Add(ChatTurnItem.FromReply(response, _memberFirstName));
        }
        catch (ApiException ex)
        {
            // The question stays in the list — retyping it would be worse than seeing why it
            // didn't get an answer. The reply slot carries the error instead of a made-up answer.
            _turns.Add(ChatTurnItem.FromError(ex.Message));
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while sending a message");
            _turns.Add(ChatTurnItem.FromError("Something went wrong sending that — try again."));
        }
        finally
        {
            ScrollToLatest();

            // Cancel before Dispose so the cycler's in-flight await throws out of its loop
            // rather than racing a disposed token source.
            waitingCts.Cancel();
            waitingCts.Dispose();
            PendingPanel.IsVisible = false;
            _isSending = false;
            SendButton.IsEnabled = true;
        }
    }

    /// <summary>Rotation just past two full breaths of <see cref="PendingBotIndicator"/>'s pulse,
    /// so each line stays long enough to read but the wait never looks stuck on one.</summary>
    private const int WaitingLineRotationMs = 3600;

    /// <summary>Shown when the waiting-sentence fetch fails — the wait still narrates itself
    /// rather than sitting silent next to the animation.</summary>
    private static readonly string[] FallbackWaitingLines =
    [
        "Looking at the readings…",
        "Checking what stands out…",
        "Putting the answer together…",
    ];

    /// <summary>
    /// Fetches the three LLM-written waiting lines for this question and rotates them through the
    /// pending panel until the send resolves and cancels this. Fire-and-forget from
    /// <see cref="SendAsync"/>: the lines are decoration, so every failure here downgrades to the
    /// canned set — and a fetch that loses the race to the reply itself simply stops.
    /// </summary>
    private async Task CycleWaitingLinesAsync(string message, CancellationToken ct)
    {
        IReadOnlyList<string> lines;
        try
        {
            var waiting = await _api.GetMemberChatWaitingSentencesAsync(
                _memberId, new MemberChatMessageRequest { Message = message }, ct);
            lines = waiting.Sentences.Count > 0 ? waiting.Sentences : FallbackWaitingLines;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            lines = FallbackWaitingLines;
        }

        for (var i = 0; !ct.IsCancellationRequested; i = (i + 1) % lines.Count)
        {
            PendingTextLabel.Text = lines[i];
            try
            {
                await Task.Delay(WaitingLineRotationMs, ct);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
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

    /// <summary>The reply's supporting series, pre-shaped for drawing — see
    /// <see cref="ChatChartItem.From"/>. Empty for user turns and errors; a resumed or refreshed
    /// reply draws the series stored with its turn.</summary>
    public IReadOnlyList<ChatChartItem> Charts { get; init; } = [];
    public bool HasCharts => Charts.Count > 0;

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
        // ElevatedCard's numbers, created per item — a Shadow is a bindable object with a
        // parent, so one shared instance cannot serve many bubbles.
        BubbleShadow = new Shadow
        {
            Brush = Microsoft.Maui.Controls.Application.Current?.Resources["CardShadowBrush"] as Brush ?? Brush.Black,
            Opacity = 0.15f,
            Radius = 14,
            Offset = new Point(0, 4),
        },
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

    public static ChatTurnItem FromReply(MemberChatMessageResponse response, string? memberFirstName)
    {
        var (drawable, summarised) = SplitCharts(response.Charts);

        return new ChatTurnItem
        {
            Content = response.Reply,
            IsUser = false,
            RoleLabel = memberFirstName is { Length: > 0 } name ? $"About {name}" : "Reply",
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

        return new ChatTurnItem
        {
            Content = turn.Content,
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
    /// First-to-last for the series <see cref="SplitCharts"/> could neither draw nor drop: two or
    /// more readings spanning too wide a window to plot.
    /// </summary>
    /// <remarks>
    /// Single-reading series no longer reach here — they are dropped, because one reading has no
    /// stretch to describe and the reply states it in prose. So there is no one-point branch: an
    /// arrow always sits between two different readings, which is the only shape that ever meant
    /// anything. Values are spelled the way the charts spell them, so a night's sleep does not
    /// read as "372" beneath a bubble that just called it six hours.
    /// </remarks>
    private static string Summarize(IReadOnlyList<ChartSeries> charts)
    {
        var parts = charts
            .Where(c => c.Points.Count > 1)
            .Select(c => $"{c.Metric}: {ChatMetricFormat.Bare(c.Metric, c.Points[0].Value)} → "
                + $"{ChatMetricFormat.Bare(c.Metric, c.Points[^1].Value)}");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// One row of the history list, display values pre-computed at construction — the same
/// converter-free convention as <see cref="ChatTurnItem"/>.
/// </summary>
public sealed class ChatSessionItem
{
    public required Guid SessionId { get; init; }

    /// <summary>The caregiver's opening question — the line a past conversation is recognised
    /// by, so it names the row.</summary>
    public required string FirstQuestion { get; init; }

    /// <summary>"Yesterday · 3 questions", or "Current conversation · 3 questions" for the row
    /// that is this thread.</summary>
    public required string Meta { get; init; }

    /// <summary>This row is the conversation the sheet is already having — tapping it returns to
    /// the live thread rather than opening a read-only copy.</summary>
    public required bool IsCurrent { get; init; }

    /// <summary>The header subtitle while this conversation is open — "From yesterday",
    /// "From Aug 21" — so the sheet says whose words the caregiver is rereading.</summary>
    public required string OpenedLabel { get; init; }

    public static ChatSessionItem From(MemberChatSessionSummaryResponse session, bool isCurrent)
    {
        // Day granularity, not clock time: the list is scanned by "which conversation was that?",
        // and per-bubble times live inside the conversation itself.
        var local = session.LastTurnAtUtc.ToLocalTime();
        var day = DateOnly.FromDateTime(local.DateTime);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var dayLabel = day == today ? "Today"
            : day == today.AddDays(-1) ? "Yesterday"
            : local.ToString("MMM d", CultureInfo.CurrentCulture);
        var questions = session.QuestionCount == 1 ? "1 question" : $"{session.QuestionCount} questions";

        return new ChatSessionItem
        {
            SessionId = session.SessionId,
            // A question that decrypts to nothing (a row from before encryption, or under a
            // rotated key) still gets a nameable row rather than a blank one.
            FirstQuestion = string.IsNullOrWhiteSpace(session.FirstQuestion)
                ? "A conversation"
                : session.FirstQuestion,
            Meta = isCurrent ? $"Current conversation · {questions}" : $"{dayLabel} · {questions}",
            IsCurrent = isCurrent,
            OpenedLabel = day == today ? "From earlier today"
                : day == today.AddDays(-1) ? "From yesterday"
                : $"From {dayLabel}",
        };
    }
}
