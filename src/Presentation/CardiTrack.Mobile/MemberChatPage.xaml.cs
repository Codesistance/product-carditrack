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
/// frame — as-built, see the design-sync backlog. Reached from Member Detail's "Ask about their
/// readings" row.
/// </summary>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(MemberName), "name")]
public partial class MemberChatPage : ContentPage
{
    /// <summary>Shell route; see <see cref="AppShell"/>.</summary>
    public const string Route = "memberchat";

    private readonly ICardiTrackApiClient _api;
    private readonly ObservableCollection<ChatTurnItem> _turns = [];

    private Guid _memberId;
    private string? _memberFirstName;
    private bool _isLoading;
    private bool _isSending;

    public MemberChatPage(ICardiTrackApiClient api)
    {
        InitializeComponent();
        _api = api;
        TurnsList.ItemsSource = _turns;
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    public string MemberName
    {
        set
        {
            _memberFirstName = Uri.UnescapeDataString(value ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(_memberFirstName))
                SubtitleLabel.Text = $"Ask about {_memberFirstName}'s readings";
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private async void OnBackTapped(object? sender, EventArgs e) =>
        await this.GoBackAsync($"{AppShell.DashboardRoute}/{CardiMemberDetailPage.Route}?memberId={_memberId}");

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private async void OnSendTapped(object? sender, EventArgs e) => await SendAsync();

    private async Task LoadAsync()
    {
        if (_isLoading)
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
            ScreenRefresh.LogFailure(ex, this, "while loading");
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

        var userTurn = ChatTurnItem.FromUserMessage(message);
        _turns.Add(userTurn);

        try
        {
            var response = await _api.SendMemberChatMessageAsync(
                _memberId, new MemberChatMessageRequest { Message = message });
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
            ScreenRefresh.LogFailure(ex, this, "while sending a message");
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
