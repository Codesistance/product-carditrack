using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Navigation;
using CardiTrack.Mobile.Core.Offline;
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

    private readonly MemberRoute _route = new();
    private string? _metricName;

    /// <summary>
    /// The metric name's half of <see cref="MemberRoute"/>'s bargain: a render that ran before
    /// the route had named a metric, and so is owed again once it does.
    /// </summary>
    private bool _renderedWithoutMetric;
    private int _days = TrendWindowSelector.DefaultDays;
    private MetricTrend? _trend;
    private CardiMemberDetailResponse? _member;

    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;

    public MetricTrendPage(ICardiTrackApiClient api)
    {
        InitializeComponent();
        _api = api;
        _feedback = new RefreshFeedback(SavedBanner, Updating);
        WindowPicker.WindowChanged += OnWindowChanged;

        // This card is the expanded view; offering to expand it again would push another copy of
        // this page on for every tap.
        Card.ShowExpand = false;
    }

    /// <summary>
    /// Whose trend this is. Shell may set this after the page has already appeared and tried
    /// to load, so an arrival that leaves a load owed runs it — see <see cref="MemberRoute"/>.
    /// </summary>
    public string MemberId
    {
        set
        {
            if (_route.Accept(value))
                _ = LoadAsync();
        }
    }

    /// <summary>
    /// Which trend to draw. Rides the same route as the member id and can land just as late —
    /// and a render that ran without it said "This trend isn't available for them", which is a
    /// statement about the member rather than about the route. So, like the id, a name that
    /// arrives after a render went without one runs the load again.
    /// </summary>
    public string MetricName
    {
        set
        {
            var name = Uri.UnescapeDataString(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(name) || name == _metricName)
                return;

            _metricName = name;
            TitleLabel.Text = name;

            if (_renderedWithoutMetric)
            {
                _renderedWithoutMetric = false;
                _ = LoadAsync();
            }
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
        await this.GoBackAsync($"{AppShell.DashboardRoute}/{CardiMemberDetailPage.Route}?memberId={_route.Id}");

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
        if (_gate.IsLoading)
            return;

        // Nothing to ask about yet. The empty id is not a member the API can refuse politely —
        // every CardiMember endpoint answers it with "CardiMember not found", which reads as
        // this member being gone — so the request is not made at all, and the arrival that
        // brings the id runs this again.
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

        try
        {
            // The saved profile's series first, the live one behind it. The chart refreshes in
            // place, so the second render is cheap; when nothing new has synced it is skipped.
            var outcome = await SnapshotRefresh.RunAsync(
                _api, _gate, ticket,
                peek: _member is null ? ct => _api.PeekCardiMemberAsync(memberId, ct) : null,
                fetch: ct => _api.GetCardiMemberAsync(memberId, ct),
                render: member =>
                {
                    _member = member;
                    Apply(member);
                },
                _feedback,
                // What Apply draws: the member's name and the metric series behind the chart.
                // Everything else on the member payload — notes, contacts, weather, baseline —
                // would otherwise redraw this chart for a change it does not show.
                sameAs: (a, b) => SamePayload.Same(new { a.Name, a.Metrics }, new { b.Name, b.Metrics }));

            if (outcome.Result == RefreshResult.NothingAndFailed)
            {
                _member = null;
                ErrorDetailLabel.Text = outcome.Error!.Message;
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
            _gate.Release(ticket);
        }
    }

    private void Apply(CardiMemberDetailResponse member)
    {
        if (TrendMetricCatalogue.ByName(_metricName) is not { } entry
            || member.Metrics is not { } metrics)
        {
            // A route naming a metric this build does not carry, or a member with nothing
            // recorded — or the route simply not having named one yet, which is not the same
            // thing and is the setter's to put right.
            _renderedWithoutMetric = string.IsNullOrWhiteSpace(_metricName);
            ErrorDetailLabel.Text = _renderedWithoutMetric
                ? "We couldn't tell which trend this is — go back and try again."
                : "This trend isn't available for them.";
            SetState(error: true);
            return;
        }

        var firstName = NameFormatting.FirstName(member.Name);
        ChatBot.MemberId = _route.Id;
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
