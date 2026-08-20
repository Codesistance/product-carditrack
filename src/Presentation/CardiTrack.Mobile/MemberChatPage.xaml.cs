using System.Collections.ObjectModel;
using System.Globalization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
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

    private readonly ICardiTrackApiClient _api;
    private readonly ObservableCollection<ChatTurnItem> _turns = [];

    private readonly Guid _memberId;
    private readonly string? _memberFirstName;
    private bool _isLoading;
    private bool _isSending;

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

        if (!string.IsNullOrWhiteSpace(memberFirstName))
            SubtitleLabel.Text = $"What would you like to know about {memberFirstName}?";

        // No OnAppearing on a ContentView — the host adds this to its tree only at the moment
        // it's shown (see MemberChatLauncher), so construction time is the right time to load.
        _loadTask = LoadAsync();
    }

    private void OnBackTapped(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Tapping the dimmed area outside the sheet dismisses it — same convention as
    /// AppPopupPage's scrim.</summary>
    private void OnScrimTapped(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnRetryClicked(object? sender, EventArgs e) => _loadTask = LoadAsync();

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        var load = LoadAsync();
        _loadTask = load;
        await load;
        Refresher.IsRefreshing = false;
    }

    private async void OnSendTapped(object? sender, EventArgs e)
    {
        _ = BounceSendIconAsync();
        await SendAsync();
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

            SetState(loaded: true);
        }
        catch (ApiException ex)
        {
            if (_turns.Count == 0)
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
            if (_turns.Count == 0)
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

    private async Task SendAsync()
    {
        if (_isSending)
            return;

        var message = MessageEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(message))
            return;

        _isSending = true;
        SendButton.IsEnabled = false;
        MessageEditor.Text = string.Empty;

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
        // for that long reads as a swallowed message, so the slot says it's being worked on.
        var pending = ChatTurnItem.Pending(_memberFirstName);
        _turns.Add(pending);

        try
        {
            var response = await _api.SendMemberChatMessageAsync(
                _memberId, new MemberChatMessageRequest { Message = message });
            _turns.Remove(pending);
            _turns.Add(ChatTurnItem.FromReply(response, _memberFirstName));
        }
        catch (ApiException ex)
        {
            // The question stays in the list — retyping it would be worse than seeing why it
            // didn't get an answer. The reply slot carries the error instead of a made-up answer.
            _turns.Remove(pending);
            _turns.Add(ChatTurnItem.FromError(ex.Message));
        }
        catch (Exception ex)
        {
            ScreenRefresh.LogFailure(ex, nameof(MemberChatPage), "while sending a message");
            _turns.Remove(pending);
            _turns.Add(ChatTurnItem.FromError("Something went wrong sending that — try again."));
        }
        finally
        {
            _isSending = false;
            SendButton.IsEnabled = true;
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
    public required Color TextColor { get; init; }
    public required Color BubbleBackground { get; init; }
    public required LayoutOptions RowAlignment { get; init; }
    public string ChartSummary { get; init; } = string.Empty;
    public bool HasChartSummary => !string.IsNullOrEmpty(ChartSummary);

    public static ChatTurnItem FromUserMessage(string content) => new()
    {
        Content = content,
        IsUser = true,
        RoleLabel = "You",
        ShowRoleLabel = false,
        TextColor = Colors.White,
        BubbleBackground = Microsoft.Maui.Controls.Application.Current?.Resources["Primary"] as Color ?? Colors.Blue,
        RowAlignment = LayoutOptions.End,
    };

    public static ChatTurnItem FromReply(MemberChatMessageResponse response, string? memberFirstName) => new()
    {
        Content = response.Reply,
        IsUser = false,
        RoleLabel = memberFirstName is { Length: > 0 } name ? $"About {name}" : "Reply",
        ShowRoleLabel = true,
        TextColor = Microsoft.Maui.Controls.Application.Current?.Resources["HeadingText"] as Color ?? Colors.Black,
        BubbleBackground = Microsoft.Maui.Controls.Application.Current?.Resources["White"] as Color ?? Colors.White,
        RowAlignment = LayoutOptions.Start,
        ChartSummary = Summarize(response.Charts),
    };

    /// <summary>The reply slot while the answer is being generated — removed and replaced by
    /// <see cref="FromReply"/> or <see cref="FromError"/> when the send resolves.</summary>
    public static ChatTurnItem Pending(string? memberFirstName) => new()
    {
        Content = "Looking at the readings…",
        IsUser = false,
        RoleLabel = memberFirstName is { Length: > 0 } name ? $"About {name}" : "Reply",
        ShowRoleLabel = true,
        TextColor = Microsoft.Maui.Controls.Application.Current?.Resources["BodyText"] as Color ?? Colors.Gray,
        BubbleBackground = Microsoft.Maui.Controls.Application.Current?.Resources["White"] as Color ?? Colors.White,
        RowAlignment = LayoutOptions.Start,
    };

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

    public static ChatTurnItem FromHistory(MemberChatTurnResponse turn, string? memberFirstName) =>
        turn.Role == "User"
            ? FromUserMessage(turn.Content)
            : new ChatTurnItem
            {
                Content = turn.Content,
                IsUser = false,
                RoleLabel = memberFirstName is { Length: > 0 } name ? $"About {name}" : "Reply",
                ShowRoleLabel = true,
                TextColor = Microsoft.Maui.Controls.Application.Current?.Resources["HeadingText"] as Color ?? Colors.Black,
                BubbleBackground = Microsoft.Maui.Controls.Application.Current?.Resources["White"] as Color ?? Colors.White,
                RowAlignment = LayoutOptions.Start,
            };

    /// <summary>First-to-last per series, e.g. "Steps: 3,201 → 5,110 · Resting heart rate: 61 → 58"
    /// — a compact stand-in for a true chart. See the member-chat plan's mobile-milestone note:
    /// full chart rendering was deliberately deferred rather than force-fit into the Dashboard's
    /// DashboardMetric-coupled trend control, which this data doesn't shape-match.</summary>
    private static string Summarize(IReadOnlyList<ChartSeries> charts)
    {
        var parts = charts
            .Where(c => c.Points.Count > 0)
            .Select(c => $"{c.Metric}: {c.Points[0].Value.ToString("0.#", CultureInfo.InvariantCulture)} → "
                + $"{c.Points[^1].Value.ToString("0.#", CultureInfo.InvariantCulture)}");
        return string.Join(" · ", parts);
    }
}
