using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Charts;
using CardiTrack.Mobile.Core.Export;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Core.Navigation;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// One journal entry in full — a Daybook, a Weekbook or a Monthbook, chosen by the cadence
/// parameter: the account, the suggestion, and the charts of the period it accounts for to read
/// it against (a fortnight for a day or a week, thirty days for a month) — each with the
/// member's own usual dashed, the published band shaded with its source named, and a counted
/// awareness line. Pushed above the Journal tab the way an alert's detail is pushed above the
/// Alerts list.
/// </summary>
/// <remarks>
/// The charts draw the fortnight (a month, for a Monthbook) ending on the day the review
/// describes, and the section title names that day. A daybook is a closure summary: days after
/// its day — today's running totals included — are of no consequence to it and are never drawn.
/// So the series is asked for ending on the entry's own day rather than today: the dashboard's
/// live series runs a fixed window back from now, and a Monthbook's month — or a Weekbook a
/// fortnight old — lies entirely outside it. Should a series still fall short of the window, the
/// charts are absent rather than quietly shifted toward the present. The awareness lines count
/// exactly the days the chart draws, so a caregiver can check every claim against the picture
/// beside it.
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
    private readonly IJournalExportFlow _export;

    private readonly MemberRoute _route = new();
    private DateOnly _date;

    /// <summary>
    /// The date's half of <see cref="MemberRoute"/>'s bargain: a load that ran before the route
    /// had handed one over, and so is owed again once it does.
    /// </summary>
    private bool _loadedWithoutDate;
    private bool _returningFromPopup;
    private bool _headerPersonalised;

    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;

    /// <summary>
    /// What one load reads: the review is the page; the member is the charts, and may be absent
    /// — a charts fetch that failed hides the section rather than costing the caregiver the
    /// review they came for.
    /// </summary>
    private sealed record EntryLoad(DigestResponse Review, CardiMemberDetailResponse? Member);

    /// <summary>The last load put on screen, saved or live — null until something is.</summary>
    private EntryLoad? _last;

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

    public JournalEntryPage(ICardiTrackApiClient api, IPopupService popups, IJournalExportFlow export)
    {
        InitializeComponent();
        this.HoldUntilInsetsApplied();
        _api = api;
        _popups = popups;
        _export = export;
        _feedback = new RefreshFeedback(SavedBanner, Updating);
    }

    /// <summary>
    /// Whose book this is. Shell may set this after the page has already appeared and tried to
    /// load, so an arrival that leaves a load owed runs it — see <see cref="MemberRoute"/>.
    /// Before this, a late id left the page on its skeleton for good: the load below simply
    /// returned, and nothing ever called it again.
    /// </summary>
    public string MemberId
    {
        set
        {
            if (_route.Accept(value))
                this.WhenRouteHasLanded(() => _ = LoadAsync(force: true));
        }
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
            // The line under the title names the period, or a Monthbook would announce itself
            // as one day's reading.
            HeaderSubtitle.Text = $"One {JournalPage.PeriodNoun(_cadence)}, read against what is usual for them";
            HeaderTitle.Text = _headerPersonalised && _memberFirstName is { } name
                ? $"{name}'s {_cadence.EntryName()}"
                : _cadence.EntryName();
            DescribeExport();
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

    /// <summary>
    /// Which day (or week, or month) this entry accounts for. The load needs this as much as it
    /// needs the member id, and Shell sets the two independently — so, like the id, a date that
    /// lands after the page has already tried to load runs it again rather than leaving the
    /// skeleton up for good.
    /// </summary>
    public string Date
    {
        set
        {
            var parsed = DateOnly.TryParseExact(
                Uri.UnescapeDataString(value ?? string.Empty), "yyyy-MM-dd", out var date)
                ? date
                : default;

            // An unusable value leaves the page as it was, for the reason MemberRoute.Accept
            // keeps a good id: emptying what the page already has takes a working screen down.
            if (parsed == default || parsed == _date)
                return;

            var owed = _loadedWithoutDate;
            _loadedWithoutDate = false;
            _date = parsed;

            if (owed)
                this.WhenRouteHasLanded(() => _ = LoadAsync(force: true));
        }
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
        if (!_popups.IsShowing && !ScreenRefresh.IsOnScreen(this))
            _export.Cancel();
    }

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync(force: true);
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync(force: true);

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.JournalRoute);

    /// <summary>
    /// This one entry — journals only, this day (or week/month), this book.
    /// Consent is the two pop-ups; the file never goes through M1-17.
    /// </summary>
    private async void OnExportTapped(object? sender, EventArgs e)
    {
        if (_route.IsMissing || _date == default)
            return;

        var name = _memberFirstName ?? "CardiJournal";
        await _export.RunAsync(
            _route.Id,
            name,
            _date,
            _date,
            JournalExportRequests.Audience(_cadence),
            _date,
            Updating);
    }

    /// <summary>
    /// Names the book in the hint rather than in the label. The control's word stays "Export"
    /// wherever it appears — that is what makes one action look like one action — and which
    /// book this is is already the heading two lines above it.
    /// </summary>
    private void DescribeExport() =>
        SemanticProperties.SetHint(
            ExportHit, $"Saves or shares this {_cadence.EntryName()}");

    /// <param name="force">
    /// Supersedes a load already in flight rather than skipping — for anything the caregiver
    /// asked for by hand. A gesture that did nothing because a slow request happened to be
    /// running is a gesture they will make again.
    /// </param>
    private async Task LoadAsync(bool force = false)
    {
        if (_gate.IsLoading && !force)
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

        // The date rides in on the same route and can land just as late. Same bargain as the
        // id: say nothing was loaded, and the setter that brings it runs this again. Its own
        // wording, though — the member is known by here, and it is the day that is not.
        if (_date == default)
        {
            _loadedWithoutDate = true;
            ErrorDetailLabel.Text = "We couldn't tell which day this is — go back and try again.";
            SetState(error: true);
            return;
        }

        var ticket = _gate.Begin();
        var (memberId, cadence, date) = (_route.Id, _cadence, _date);

        if (_last is null)
            SetState(loading: true);

        try
        {
            // The review is the page; the member detail is the charts. Sequential rather than
            // parallel so a failure has one story — and the charts degrade to absent rather than
            // costing the caregiver the review they came for. A finished day's entry never
            // changes, so when the live answer is the saved one over again nothing is redrawn and
            // nothing is announced; only the charts' window moving on counts as new.
            var outcome = await SnapshotRefresh.RunAsync<EntryLoad>(
                _api, _gate, ticket,
                peek: _last is null
                    ? async (ct, scope) =>
                    {
                        var review = await scope.Track(_api.PeekJournalEntryAsync(memberId, cadence, date, ct));
                        if (review is null)
                            return null;
                        // Not tracked, for the same reason the live member call below is not:
                        // the review is what this page is, so the review's provenance is the
                        // page's. Tracking the charts would date the banner from whichever of
                        // the two was saved longer ago and could call the page offline over a
                        // review the device saved a minute earlier.
                        //
                        // The live profile the device holds: the series ending on the entry's
                        // day is never cached, so offline the charts come from a series that
                        // reaches an entry only while it is recent — and the window check hides
                        // them when it does not, rather than drawing days the entry is not about.
                        var member = await _api.PeekCardiMemberAsync(memberId, ct);
                        return new EntryLoad(review, member);
                    }
                    : null,
                fetch: async (ct, scope) =>
                {
                    var review = await scope.Track(_api.GetJournalEntryAsync(memberId, cadence, date, ct));
                    CardiMemberDetailResponse? member = null;
                    try
                    {
                        // Not tracked: the review is what the page is, so the review's provenance
                        // is the page's — charts served from the device beside a live review are
                        // still charts, not a reason to call the page offline.
                        //
                        // The series ending on the entry's own day, so a month-old Monthbook
                        // draws its month rather than nothing.
                        member = await _api.GetCardiMemberAsync(memberId, date, ct);
                    }
                    catch (ApiException ex) when (!ct.IsCancellationRequested)
                    {
                        // The review stands on its own. Offline, the live profile the device
                        // holds, if any, draws what charts its series still reaches — the same
                        // fallback the first load makes. Any other refusal (access gone, member
                        // removed) leaves the section absent: a saved profile must not stand in
                        // for one the server has just declined to give.
                        if (ex.IsNetworkFailure)
                            member = await _api.PeekCardiMemberAsync(memberId, ct);
                    }
                    return new EntryLoad(review, member);
                },
                render: load =>
                {
                    _last = load;
                    Apply(load.Review);
                    ApplyMember(load.Member);
                    SetState(loaded: true);
                },
                _feedback,
                // The whole review — it is the page — plus only what the charts draw from the
                // member. A finished day's entry never changes, so this page should be silent on
                // reopening unless the fortnight behind it has actually moved.
                sameAs: (a, b) => SamePayload.Same(
                    new { a.Review, a.Member?.Name, a.Member?.Metrics },
                    new { b.Review, b.Member?.Name, b.Member?.Metrics }));

            switch (outcome.Result)
            {
                case RefreshResult.Superseded:
                    return;
                case RefreshResult.NothingAndFailed:
                    _last = null;
                    ErrorDetailLabel.Text = outcome.Error!.IsNotFound
                        ? $"No {cadence.EntryName()} was written for this {JournalPage.PeriodNoun(cadence)}."
                        : outcome.Error.Message;
                    SetState(error: true);
                    return;
            }

            if (outcome.IsSavedOnly && outcome.Error is not null)
                await _popups.ShowWarningAsync(outcome.Error.Message, "Couldn't refresh");
        }
        finally
        {
            // Only the load that still owns the screen clears the pull spinner. A superseded one
            // stopping it would take the spinner off a pull that is still running.
            if (_gate.IsCurrent(ticket))
                Refresher.IsRefreshing = false;
            _gate.Release(ticket);
        }
    }

    /// <summary>The charts half of a load, or their absence when the member could not be read.</summary>
    private void ApplyMember(CardiMemberDetailResponse? member)
    {
        if (member is null)
        {
            TrendsTitle.IsVisible = false;
            TrendsHost.IsVisible = false;
            AwarenessFooter.IsVisible = false;
            return;
        }

        ChatBot.MemberId = _route.Id;
        ChatBot.MemberFirstName = member.DisplayFirstName();
        if (!_headerPersonalised)
            ApplyHeaderName(member.DisplayFirstName());
        ApplyTrends(member.Metrics);
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

        // A journal entry is read after the fact, about a period that has closed, so "what was
        // that day exactly?" is the question it most invites and the one the prose deliberately
        // does not answer number by number. Same callout the member detail and chat charts give,
        // spelled with this chart's own axis format.
        var chart = new TrendChart { HeightRequest = 130, Interactive = true };
        chart.ValueFormatter = value => string.Format(axisFormat, (decimal)value);
        SemanticProperties.SetHint(chart, "Tap a reading to see its value");
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

        // One line per mark the chart drew, led by that mark's own swatch — the key and the count
        // fused. They used to be two things: a key line naming each figure ("Dashed: their usual
        // 5.5h · Shaded: recommended 7–8h (NSF)") and then a sentence per mark naming the same
        // figure again, so every number under the chart was said twice. Now the sentence is the
        // key: the swatch says which mark it is about, the words say the figure and how the days
        // fell against it. A mark with no count to give — too few measured days, or a metric that
        // is not counted against it — still gets its line, with the figure alone, since the chart
        // draws it and a mark nobody names is one the caregiver has to guess at.
        var ink = MetricStatus.Resource(inkKey, Colors.Gray);

        if (metric.Baseline is { } usual)
        {
            // The figure rides in the sentence — "their usual 4h" — so the claim can be checked
            // against the dashed rule without a key to look it up in.
            var usualText = string.Create(
                System.Globalization.CultureInfo.CurrentCulture,
                $"their usual {string.Format(axisFormat, usual)}{unit}");

            var usualLine = usualDirection is { } direction
                ? TrendAwareness.Line(window, usual, direction, usualText, dayWord, windowDays)
                : null;
            stack.Add(MarkLine(
                new TrendLegendSwatch(TrendLegendMark.Baseline) { Ink = ink },
                usualLine ?? Capitalized(usualText)));
        }

        if (metric.Reference is { } published)
        {
            string? bandLine = null;
            if (band is not null)
            {
                var bound = band.AgainstLow ? published.Low : published.High;
                var boundText = string.Create(
                    System.Globalization.CultureInfo.CurrentCulture,
                    $"the recommended {string.Format(axisFormat, bound)}{unit} ({published.Source})");
                bandLine = TrendAwareness.BandLine(window, bound, band.Direction, boundText, dayWord, windowDays);
            }

            // The whole band when there is no count to give: it is what the shading covers, and it
            // carries its publisher — a shaded band with nobody's name on it is a claim wearing no
            // authority.
            var rangeText = string.Create(
                System.Globalization.CultureInfo.CurrentCulture,
                $"Recommended {string.Format(axisFormat, published.Low)}–{string.Format(axisFormat, published.High)}{unit} ({published.Source})");
            stack.Add(MarkLine(new TrendLegendSwatch(TrendLegendMark.Reference), bandLine ?? rangeText));
        }

        return new Border { Style = Styled("ElevatedCard"), Content = stack };
    }

    /// <summary>A mark's swatch and the sentence about it, the swatch on the sentence's first line.</summary>
    private static View MarkLine(View swatch, string text)
    {
        swatch.VerticalOptions = LayoutOptions.Start;
        swatch.Margin = new Thickness(0, 5, 0, 0);
        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 8,
        };
        row.Add(swatch);
        row.Add(new Label { Text = text, Style = Styled("Body2Dark") }, 1);
        return row;
    }

    private static string Capitalized(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

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
