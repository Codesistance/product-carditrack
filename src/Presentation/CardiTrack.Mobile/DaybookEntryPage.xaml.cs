using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Charts;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// One day's review in full: the account, the suggestion, and the trailing fortnight's charts to
/// read it against — each with the member's own usual dashed, the published band shaded with its
/// source named, and a counted awareness line. Pushed above the Daybook tab the way an alert's
/// detail is pushed above the Alerts list.
/// </summary>
/// <remarks>
/// The charts draw the <em>current</em> fortnight whatever day the review describes, and the
/// section title says so. The dashboard series always runs to today, so a chart scoped to an old
/// review's own window would be drawn from data the series no longer carries — and the awareness
/// lines count exactly the days the chart draws, so a caregiver can check every claim against the
/// picture beside it. Counts, never scores: the release matrix's standing decision is that trend
/// interpretation carries no risk scores, and the footer states the register plainly.
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(Date), "date")]
public partial class DaybookEntryPage : ContentPage
{
    public const string Route = "daybookentry";

    /// <summary>The charts' window and the awareness count's — one number, so they cannot drift.</summary>
    private const int ChartDays = TrendAwareness.WindowDays;

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private Guid _memberId;
    private DateOnly _date;
    private bool _isLoading;
    private bool _returningFromPopup;
    private bool _hasLoadedOnce;

    public DaybookEntryPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    public string Date
    {
        set => _date = DateOnly.TryParseExact(
            Uri.UnescapeDataString(value ?? string.Empty), "yyyy-MM-dd", out var date)
            ? date
            : default;
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

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.DaybookRoute);

    private async Task LoadAsync()
    {
        if (_isLoading || _memberId == Guid.Empty || _date == default)
            return;
        _isLoading = true;

        if (!_hasLoadedOnce)
            SetState(loading: true);

        try
        {
            // The review is the page; the member detail is the charts. Sequential rather than
            // parallel so a failure has one story — and the charts degrade to absent rather than
            // costing the caregiver the review they came for.
            var review = await _api.GetDaybookAsync(_memberId, _date);
            Apply(review);

            try
            {
                var member = await _api.GetCardiMemberAsync(_memberId);
                ApplyTrends(member.Metrics);
            }
            catch (ApiException)
            {
                // The review stands on its own; a charts fetch that failed hides the section
                // rather than replacing a loaded review with an error panel.
                TrendsHost.IsVisible = false;
                AwarenessFooter.IsVisible = false;
            }

            _hasLoadedOnce = true;
            SetState(loaded: true);
        }
        catch (ApiException ex)
        {
            if (!_hasLoadedOnce)
            {
                ErrorDetailLabel.Text = ex.IsNotFound
                    ? "No daybook entry was written for this day."
                    : ex.Message;
                SetState(error: true);
            }
            else
            {
                await _popups.ShowWarningAsync(ex.Message, "Couldn't refresh");
            }
        }
        finally
        {
            _isLoading = false;
            Refresher.IsRefreshing = false;
        }
    }

    private void Apply(DigestResponse review)
    {
        DayLabel.Text = DaybookPresentation.DayLabel(review.LocalDate);
        HeadlineLabel.Text = string.IsNullOrWhiteSpace(review.Headline)
            ? "The day in full"
            : review.Headline;
        TextLabel.Text = review.Text;

        PillHost.Clear();
        if (DaybookPresentation.UrgencyPill(review.Urgency) is { } pill)
            PillHost.Add(pill);

        var hasSuggestion = !string.IsNullOrWhiteSpace(review.Suggestion);
        SuggestionDivider.IsVisible = hasSuggestion;
        SuggestionTitle.IsVisible = hasSuggestion;
        SuggestionLabel.IsVisible = hasSuggestion;
        SuggestionLabel.Text = review.Suggestion ?? string.Empty;

        GeneratedLabel.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"Written {review.GeneratedAtUtc.ToLocalTime():d MMMM 'at' HH:mm}");
    }

    /// <summary>
    /// The three metrics the review's register anchors on — sleep, resting heart rate, movement —
    /// as fortnight charts. A metric whose series holds nothing in the window is skipped whole:
    /// an empty chart under a full account reads as data lost rather than data absent, and the
    /// review's own text already says what was not measured.
    /// </summary>
    private void ApplyTrends(DashboardMetrics? metrics)
    {
        TrendsHost.Clear();

        if (metrics is null)
        {
            TrendsHost.IsVisible = false;
            AwarenessFooter.IsVisible = false;
            return;
        }

        var blocks = new[]
        {
            Block(metrics.Sleep, "Sleep", "MetricSleepInk", "{0:0.#}",
                TrendAwareness.Direction.BelowUsual, "night"),
            Block(metrics.RestingHeartRate, "Resting heart rate", "MetricHeartInk", "{0:N0}",
                TrendAwareness.Direction.AboveUsual, "day"),
            Block(metrics.Steps, "Steps", "MetricStepsInk", "{0:N0}",
                TrendAwareness.Direction.BelowUsual, "day"),
        };

        foreach (var block in blocks)
        {
            if (block is not null)
                TrendsHost.Add(block);
        }

        var any = TrendsHost.Children.Count > 0;
        TrendsHost.IsVisible = any;
        AwarenessFooter.IsVisible = any;
    }

    private static View? Block(
        DashboardMetric metric,
        string name,
        string inkKey,
        string axisFormat,
        TrendAwareness.Direction direction,
        string dayWord)
    {
        var window = metric.Series.TakeLast(ChartDays).ToList();
        var values = window.Where(p => p.Value is not null).Select(p => (double)p.Value!).ToList();
        if (values.Count == 0)
            return null;

        var baseline = metric.Baseline is { } b ? (double)b : (double?)null;
        var reference = metric.Reference;
        var scale = TrendScale.For(
            values.Min(),
            values.Max(),
            baseline,
            reference is not null ? (double)reference.Low : null,
            reference is not null ? (double)reference.High : null);

        var chart = new TrendChart { HeightRequest = 130 };
        chart.Render(
            window,
            scale,
            MetricStatus.Resource(inkKey, Colors.Gray),
            showMarkers: true,
            baseline: metric.Baseline,
            reference: reference);

        var stack = new VerticalStackLayout { Spacing = 6 };
        stack.Add(new Label { Text = name, Style = Styled("Body1SemiBoldDark") });
        stack.Add(chart);

        // The key names only marks the chart actually drew, and the band carries its publisher —
        // a shaded band with nobody's name on it is a claim wearing no authority.
        if (MetricChartKey.For(metric, axisFormat) is { } key)
        {
            stack.Add(new Label
            {
                Text = key,
                Style = Styled("MetricRowCaption"),
                TextColor = Tinted("MutedText"),
                LineBreakMode = LineBreakMode.WordWrap,
            });
        }

        // A count the caregiver can check against the chart above it — never a score.
        if (TrendAwareness.Line(window, metric.Baseline, direction, dayWord) is { } line)
        {
            stack.Add(new Label
            {
                Text = line,
                Style = Styled("Body2Dark"),
            });
        }

        return new Border { Style = Styled("ElevatedCard"), Content = stack };
    }

    private static Style Styled(string key) =>
        (Style)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    private static Color Tinted(string key) =>
        (Color)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
    }
}
