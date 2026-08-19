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
/// The charts draw the fortnight ending on the day the review describes, and the section title
/// names that day. A daybook is a closure summary: days after its day — today's running totals
/// included — are of no consequence to it and are never drawn. The dashboard series runs a fixed
/// window back from today, so an entry old enough to have fallen off its reach shows no charts at
/// all rather than a window quietly shifted toward the present. The awareness lines count exactly
/// the days the chart draws, so a caregiver can check every claim against the picture beside it.
/// Counts, never scores: the release matrix's standing decision is that trend interpretation
/// carries no risk scores, and the footer states the register plainly.
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(Date), "date")]
[QueryProperty(nameof(MemberName), "name")]
[QueryProperty(nameof(Cadence), "cadence")]
public partial class JournalEntryPage : ContentPage
{
    public const string Route = "journalentry";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private Guid _memberId;
    private DateOnly _date;
    private bool _isLoading;
    private bool _returningFromPopup;
    private bool _hasLoadedOnce;
    private bool _headerPersonalised;

    /// <summary>
    /// Which book this entry is. Defaults to the Daybook so a link that predates the cadence
    /// parameter still opens the series it was written for.
    /// </summary>
    private JournalCadence _cadence = JournalCadence.Daybook;

    /// <summary>
    /// How many days this entry's charts draw. A month's account is read against the month; a day's
    /// and a week's against the fortnight, which for a Weekbook lands as this week beside the last.
    /// </summary>
    private int ChartWindowDays => _cadence == JournalCadence.Monthbook
        ? TrendAwareness.MonthWindowDays
        : TrendAwareness.WindowDays;

    public JournalEntryPage(ICardiTrackApiClient api, IPopupService popups)
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

    /// <summary>
    /// Which book to read. Set before <see cref="MemberName"/> matters, since the header names the
    /// book — Shell applies query properties in declaration order, and the cadence is declared
    /// last so it must not be what the header waits on. Re-applying the name after it lands keeps
    /// the two independent of that order.
    /// </summary>
    public string Cadence
    {
        set
        {
            _cadence = JournalCadenceExtensions.ParseCadence(Uri.UnescapeDataString(value ?? string.Empty));
            HeaderTitle.Text = _headerPersonalised && _memberFirstName is { } name
                ? $"{name}'s {_cadence.EntryName()}"
                : _cadence.EntryName();
        }
    }

    /// <summary>
    /// The member's first name, passed by the list so the header can say whose entry this is
    /// from the first frame. Optional: a deep link without it keeps the bare book name until the
    /// member fetch supplies the name.
    /// </summary>
    public string MemberName
    {
        set => ApplyHeaderName(Uri.UnescapeDataString(value ?? string.Empty));
    }

    private string? _memberFirstName;

    private void ApplyHeaderName(string? firstName)
    {
        if (string.IsNullOrWhiteSpace(firstName))
            return;

        _memberFirstName = firstName;
        HeaderTitle.Text = $"{firstName}'s {_cadence.EntryName()}";
        _headerPersonalised = true;
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
        await this.GoBackAsync(AppShell.JournalRoute);

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
            var review = await _api.GetJournalEntryAsync(_memberId, _cadence, _date);
            Apply(review);

            try
            {
                var member = await _api.GetCardiMemberAsync(_memberId);
                if (!_headerPersonalised)
                    ApplyHeaderName(NameFormatting.FirstName(member.Name));
                ApplyTrends(member.Metrics);
            }
            catch (ApiException)
            {
                // The review stands on its own; a charts fetch that failed hides the section
                // rather than replacing a loaded review with an error panel.
                TrendsTitle.IsVisible = false;
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
                    ? $"No {_cadence.EntryName()} was written for this {JournalPage.PeriodNoun(_cadence)}."
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
        DayLabel.Text = JournalPresentation.PeriodLabel(_cadence, review.LocalDate);
        // Entries written before headlines existed have none, and the page titles itself in that
        // case — at its own cadence, or a Weekbook opened from the Weeks list would announce
        // itself as a day.
        HeadlineLabel.Text = string.IsNullOrWhiteSpace(review.Headline)
            ? JournalPage.FallbackHeadline(_cadence)
            : review.Headline;
        TextLabel.Text = review.Text;

        // The rail, not a pill — the same left-edge colouring the list tiles and the alert
        // tiles wear. White when the model returned no urgency, so the card simply has no rail
        // rather than a grey one implying a tier nobody judged.
        EntryRail.BackgroundColor =
            JournalPresentation.UrgencyRailColor(review.Urgency) ?? Tinted("White");

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
            TrendsTitle.IsVisible = false;
            TrendsHost.IsVisible = false;
            AwarenessFooter.IsVisible = false;
            return;
        }

        // The title names the window's end — the entry's own last day, said the way the header
        // says it — because the fortnight ends there whatever the calendar has done since.
        //
        // On a Weekbook that window is exactly this week and the one before it, since the entry is
        // dated by its week's last day: the fortnight the Daybook uses for context happens to be
        // the week-against-last-week comparison a week's account wants, at no extra cost.
        var dayLabel = JournalPresentation.DayLabel(_date);
        var endLabel = dayLabel is "Today" or "Yesterday" ? dayLabel.ToLowerInvariant() : dayLabel;

        TrendsTitle.Text = _cadence switch
        {
            JournalCadence.Weekbook => $"This week and the one before, to {endLabel}",
            JournalCadence.Monthbook => $"The 30 days up to {endLabel}",
            _ => $"The 14 days up to {endLabel}",
        };

        // Every metric the dashboard series carries, each against whatever yardsticks it
        // honestly has: the member's own usual where one is learned, and the published band where
        // a standards body publishes one — never both invented. Steps has no published range (no
        // body publishes a daily step count) and skin temperature has no published band and no
        // single concerning direction, so each carries only the lines it has earned.
        var window = ChartWindowDays;
        var blocks = new[]
        {
            Block(metrics.Sleep, _date, "Sleep", "MetricSleepInk", "{0:0.#}", "h",
                TrendAwareness.Direction.BelowUsual, "night",
                new BandCount(TrendAwareness.Direction.BelowUsual, AgainstLow: true), window),
            Block(metrics.RestingHeartRate, _date, "Resting heart rate", "MetricHeartInk", "{0:N0}", " bpm",
                TrendAwareness.Direction.AboveUsual, "day",
                new BandCount(TrendAwareness.Direction.AboveUsual, AgainstLow: false), window),
            Block(metrics.SpO2, _date, "Blood oxygen", "MetricSpO2Ink", "{0:0.#}", "%",
                TrendAwareness.Direction.BelowUsual, "day",
                new BandCount(TrendAwareness.Direction.BelowUsual, AgainstLow: true), window),
            Block(metrics.BreathingRate, _date, "Breathing rate", "MetricBreathingInk", "{0:0.#}", "/min",
                TrendAwareness.Direction.AboveUsual, "day",
                new BandCount(TrendAwareness.Direction.AboveUsual, AgainstLow: false), window),
            Block(metrics.Steps, _date, "Steps", "MetricStepsInk", "{0:N0}", " steps",
                TrendAwareness.Direction.BelowUsual, "day", band: null, windowDays: window),
            Block(metrics.Temperature, _date, "Skin temperature", "MetricTemperatureInk", "{0:0.#}", "°C",
                usualDirection: null, "day", band: null, windowDays: window),
        };

        foreach (var block in blocks)
        {
            if (block is not null)
                TrendsHost.Add(block);
        }

        var any = TrendsHost.Children.Count > 0;
        TrendsTitle.IsVisible = any;
        TrendsHost.IsVisible = any;
        AwarenessFooter.IsVisible = any;
    }

    /// <summary>
    /// A counted claim against a published band — which side of which bound. The bound itself
    /// always comes from the metric's own <see cref="MetricReference"/>, so the count can never
    /// cite a figure the chart does not shade, and the sentence always names the publisher.
    /// </summary>
    private sealed record BandCount(TrendAwareness.Direction Direction, bool AgainstLow);

    private static View? Block(
        DashboardMetric metric,
        DateOnly reviewedDate,
        string name,
        string inkKey,
        string axisFormat,
        string unit,
        TrendAwareness.Direction? usualDirection,
        string dayWord,
        BandCount? band,
        int windowDays)
    {
        // The entry's own window, or nothing at all: one the series no longer reaches back to is
        // an absent chart, never one shifted toward the present.
        //
        // The chart's window and the counted lines' are the same number by construction — the
        // sentence beneath a chart claims to describe it, and two windows would make that claim
        // false while both still looked plausible.
        if (TrendAwareness.WindowEndingOn(metric.Series, reviewedDate, windowDays) is not { } window)
            return null;

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

        // The extent written on the axis, top and bottom, beside the plot rather than over it —
        // the same construction the member detail's trend cards use, so a caregiver moving
        // between the two screens reads one kind of chart. Both numbers name the scale the line
        // is actually drawn against, which the baseline and the band get a say in.
        var axis = new Grid
        {
            RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Star)],
        };
        var maxLabel = AxisLabel(string.Format(axisFormat, scale.Max));
        maxLabel.VerticalOptions = LayoutOptions.Start;
        var minLabel = AxisLabel(string.Format(axisFormat, scale.Min));
        minLabel.VerticalOptions = LayoutOptions.End;
        axis.Add(maxLabel);
        axis.Add(minLabel, 0, 1);

        var plot = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 8,
        };
        plot.Add(axis);
        plot.Add(chart, 1);
        stack.Add(plot);

        // The key names only marks the chart actually drew, and the band carries its publisher —
        // a shaded band with nobody's name on it is a claim wearing no authority.
        if (MetricChartKey.For(metric, axisFormat, unit) is { } key)
        {
            stack.Add(new Label
            {
                Text = key,
                Style = Styled("MetricRowCaption"),
                TextColor = Tinted("MutedText"),
                LineBreakMode = LineBreakMode.WordWrap,
            });
        }

        // Counts the caregiver can check against the chart above them — never scores. Two
        // possible lines: against the member's own usual, and against the published band the
        // chart shades, each said only when its yardstick exists.
        if (usualDirection is { } direction && metric.Baseline is { } usual)
        {
            // The figure rides in the sentence — "their usual 4h" — so the claim can be checked
            // against the dashed rule without a round trip through the key line above it.
            var usualText = string.Create(
                System.Globalization.CultureInfo.CurrentCulture,
                $"their usual {string.Format(axisFormat, usual)}{unit}");

            if (TrendAwareness.Line(window, usual, direction, usualText, dayWord, windowDays) is { } usualLine)
            {
                stack.Add(new Label
                {
                    Text = usualLine,
                    Style = Styled("Body2Dark"),
                });
            }
        }

        if (band is not null && metric.Reference is { } published)
        {
            var bound = band.AgainstLow ? published.Low : published.High;
            var boundText = string.Create(
                System.Globalization.CultureInfo.CurrentCulture,
                $"the recommended {string.Format(axisFormat, bound)}{unit} ({published.Source})");

            if (TrendAwareness.BandLine(window, bound, band.Direction, boundText, dayWord, windowDays) is { } bandLine)
            {
                stack.Add(new Label
                {
                    Text = bandLine,
                    Style = Styled("Body2Dark"),
                });
            }
        }

        return new Border { Style = Styled("ElevatedCard"), Content = stack };
    }

    /// <summary>
    /// One axis number, dressed the way the member detail's trend cards dress theirs — muted,
    /// annotation-sized, ranged right against the plot it measures.
    /// </summary>
    private static Label AxisLabel(string text) => new()
    {
        Text = text,
        Style = Styled("Body2"),
        FontSize = 12,
        TextColor = Tinted("MutedText"),
        HorizontalTextAlignment = TextAlignment.End,
    };

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
