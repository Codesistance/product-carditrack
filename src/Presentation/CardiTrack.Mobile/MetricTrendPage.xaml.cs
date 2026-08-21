using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// One metric's trend with the whole screen to itself, reached from the expand control on its
/// carousel card.
/// </summary>
/// <remarks>
/// <para>
/// The carousel card is 330dp tall and holds six metrics side by side — right for a glance, wrong
/// for reading a month. At 30 days it puts a point every 11dp, where the line, its markers and the
/// shaded gaps all compete for the same few pixels. Here the same card gets every row the screen
/// has left.
/// </para>
/// <para>
/// The same card, not a second chart built to resemble it: the shaded runs and their tooltips, the
/// legend, the baseline rule and the reference band are all its own behaviour, and a copy would be
/// a second thing to keep in step with it.
/// </para>
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(MetricName), "metric")]
[QueryProperty(nameof(Days), "days")]
public partial class MetricTrendPage : ContentPage
{
    /// <summary>Shell route; see <see cref="AppShell"/>.</summary>
    public const string Route = "metrictrend";

    private readonly ICardiTrackApiClient _api;

    private Guid _memberId;
    private string? _metricName;
    private int _days = TrendWindowSelector.DefaultDays;
    private MetricTrend? _trend;
    private CardiMemberDetailResponse? _member;
    private bool _isLoading;

    public MetricTrendPage(ICardiTrackApiClient api)
    {
        InitializeComponent();
        _api = api;
        WindowPicker.WindowChanged += OnWindowChanged;

        // This card is the expanded view; offering to expand it again would push another copy of
        // this page on for every tap.
        Card.ShowExpand = false;
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    public string MetricName
    {
        set
        {
            _metricName = Uri.UnescapeDataString(value ?? string.Empty);
            TitleLabel.Text = _metricName;
        }
    }

    /// <summary>
    /// The window the card was showing when it was expanded, so the bigger chart opens on what the
    /// caregiver was already looking at rather than resetting them to a week.
    /// </summary>
    public string Days
    {
        set
        {
            if (int.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var days)
                && TrendWindowSelector.Windows.Contains(days))
            {
                _days = days;
                WindowPicker.Select(days);
            }
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

    /// <summary>
    /// Retunes the chart in place. The card reads its window off the item it is bound to, so
    /// setting it redraws what is already on screen — rebinding would rebuild the card and lose
    /// the scroll and the tooltip state with it.
    /// </summary>
    private void OnWindowChanged(object? sender, int days)
    {
        _days = days;
        if (_trend is not null)
            _trend.Days = days;
    }

    private async Task LoadAsync()
    {
        if (_isLoading)
            return;
        _isLoading = true;

        if (_member is null)
            SetState(loading: true);

        try
        {
            _member = await _api.GetCardiMemberAsync(_memberId);
            Apply(_member);
        }
        catch (ApiException ex)
        {
            if (_member is null)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(error: true);
            }
        }
        catch (Exception ex)
        {
            // The same stance as the other pages in this branch: a render fault reaching an async
            // void caller is otherwise silent and permanent.
            ScreenRefresh.LogFailure(ex, this, "while loading");
            if (_member is null)
            {
                ErrorDetailLabel.Text = "Something went wrong while showing this trend.";
                SetState(error: true);
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void Apply(CardiMemberDetailResponse member)
    {
        if (TrendMetricCatalogue.ByName(_metricName) is not { } entry
            || member.Metrics is not { } metrics)
        {
            // A route naming a metric this build does not carry, or a member with nothing recorded.
            ErrorDetailLabel.Text = "This trend isn't available for them.";
            SetState(error: true);
            return;
        }

        var firstName = NameFormatting.FirstName(member.Name);
        ChatBot.MemberId = _memberId;
        ChatBot.MemberFirstName = firstName;
        var reading = entry.Select(metrics);

        if (_trend is null)
        {
            _trend = new MetricTrend(
                entry.Icon, entry.Ink, entry.Name, entry.Value, entry.Axis, reading, _days, firstName);
            Card.BindingContext = _trend;
        }
        else
        {
            // Refresh in place for the same reason the window change does: the item is what the
            // card watches, so new numbers arrive without rebuilding it.
            _trend.Metric = reading;
            _trend.Days = _days;
        }

        SetState(loaded: true);
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        Card.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
        WindowPicker.IsVisible = !error;
    }
}
