using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Charts;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

public partial class MetricCard : ContentView
{
    private const int StarCount = StarRatingView.StarCount;

    /// <summary>
    /// Smallest difference a one-decimal caption can state. Anything under it would print as
    /// "0°C", which is a way of saying "no change" that looks like a measurement.
    /// </summary>
    private const decimal CaptionResolution = 0.05m;

    /// <summary>
    /// Smallest movement the trend beside a reading can state. Anything under it rounds to "0%"
    /// at the tenth <see cref="Percent"/> prints, which is an arrow pointing at no change.
    /// See <see cref="ApplyTrend"/>.
    /// </summary>
    private const decimal TrendResolution = 0.05m;

    /// <summary>
    /// The ends of the skin-temperature track (see <see cref="ApplyTemperatureTrack"/>). Wide
    /// enough to hold what a wrist wearable reads through a cold room or a warm bed, tight enough
    /// that a degree of movement is a visible step rather than a twitch.
    /// </summary>
    private const decimal SkinTempAxisLow = 30m;

    /// <inheritdoc cref="SkinTempAxisLow"/>
    private const decimal SkinTempAxisHigh = 40m;

    public MetricCard()
    {
        InitializeComponent();
    }

    public void ApplySteps(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_steps.svg";
        NameLabel.Text = "Activity";

        // The one card that states a movement between two days rather than against the member's
        // usual one. Steps accumulate, so the payload leaves ChangePercent unset while the day is
        // still running and the tile would otherwise carry no percentage at all for most of the
        // day. The bar and the caption under it already compare with the day before; the
        // percentage is that same comparison in figures, so everything on the tile answers one
        // question. The star row is the exception, and stays the rating against their usual day.
        var progress = ActivityDayProgress.For(metric);
        SetReading(
            metric.Value is { } v ? $"{v:N0} steps" : "—",
            progress?.ChangePercent,
            // Named for a screen reader the same way the caption names it in print, so the two
            // cannot describe different days.
            progress is { PreviousIsYesterday: true } ? "yesterday" : "the previous day");

        // This card carries no pill, so there is nothing on it for the star row to match and the
        // rating colours itself.
        ApplyStars(metric, matchPill: false);
        ApplyActivityTrack(progress);
    }

    public void ApplyHeartRate(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_heart.svg";
        NameLabel.Text = "Heart Rate";
        SetReading(metric.Value is { } v ? $"{v:N0} bpm" : "—", metric);

        ApplyStars(metric, matchPill: ApplyStatusPill(metric.Status));
        CaptionLabel.Text = metric is { RangeLow: { } low, RangeHigh: { } high }
            ? $"{low}-{high} bpm typical"
            : "Resting heart rate";
    }

    public void ApplySleep(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_sleep.svg";
        NameLabel.Text = "Sleep";
        var showedTrend = SetReading(metric.Value is { } v ? $"{v:0.#} hours" : "—", metric);

        // The one card whose stars and status answer different questions — the stars read how well
        // and how long the night was, the status reads its duration against the baseline — so its
        // pill names the band of the rating itself rather than the status, and the star row keeps
        // colouring itself from the same bands: the two accents agree by construction, without
        // either reading the status that may honestly disagree with both.
        ShowPill(MetricStatus.SleepQualityPill(metric.QualityScore));
        ApplyStars(metric, matchPill: false);
        // Which way the night went, when the reading is not already carrying it. Direction only,
        // no verdict — a longer night is not automatically a better one, and this caption used to
        // call twelve hours "Better than average" directly under the stars that now mark it down
        // for exactly that. The rating carries the judgement.
        //
        // Silent about direction once the percentage beside the reading states it: the two would
        // be the same sentence twice on a tile with room for neither to spare, so the caption
        // names the night instead.
        CaptionLabel.Text = showedTrend
            ? "Last night"
            : metric.ChangePercent switch
            {
                > 0 => "Longer than usual",
                < 0 => "Shorter than usual",
                0 => "In line with usual",
                _ => "Last night",
            };
    }

    public void ApplyTemperature(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_temperature.svg";
        NameLabel.Text = "Skin Temp";
        SetReading(metric.Value is { } v ? $"{v:0.#}°C" : "—", metric);

        ApplyStars(metric, matchPill: ApplyStatusPill(metric.Status));
        CaptionLabel.Text = TemperatureComparison(metric);
        ApplyTemperatureTrack(metric);
    }

    /// <summary>
    /// The bar under activity: filled against the previous calendar day's total, with the track
    /// max growing to today once today is ahead. See <see cref="ActivityDayProgress"/>.
    /// </summary>
    /// <remarks>
    /// Two stacked fills when today is ahead — yesterday's share, then the extra — so beating
    /// yesterday is visible rather than a full bar that looks like matching it. One fill while
    /// still behind or level. Hidden when day n−1 is missing or both days are zero.
    /// </remarks>
    private void ApplyActivityTrack(ActivityDayProgress? dayProgress)
    {
        if (dayProgress is not { } progress)
        {
            ProgressTrackBorder.IsVisible = false;
            MarkerGrid.IsVisible = false;
            OverflowFill.IsVisible = false;
            SemanticProperties.SetDescription(ProgressTrackBorder, null);
            CaptionLabel.Text = "Daily activity";
            return;
        }

        ProgressTrackBorder.IsVisible = true;
        MarkerGrid.IsVisible = false;
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        ProgressFill.Background = new SolidColorBrush((Color)resources["MetricStepsInk"]);
        OverflowFill.BackgroundColor = (Color)resources["MetricStepsAhead"];
        SetStack(progress.Compared, progress.Overflow);
        SemanticProperties.SetDescription(ProgressTrackBorder, progress.Description);
        CaptionLabel.Text = progress.Caption;
    }

    /// <summary>
    /// The bar under skin temperature: filled to the previous reading, marked at today's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Activity's bar fills against yesterday's total; this one has no day-total to fill against,
    /// so it fills against the last reading instead and puts today's on top of it as a tick. What
    /// it shows is therefore a movement, not an achievement: how far today sits from the night
    /// before, and which side of it. The two together answer the question the number alone cannot
    /// — 34°C is meaningless in isolation, "a little above where it was last night" is not.
    /// </para>
    /// <para>
    /// The axis is fixed at <see cref="SkinTempAxisLow"/>–<see cref="SkinTempAxisHigh"/>°C rather
    /// than scaled to the readings in hand, so the same movement is always the same width: a
    /// tenth of a degree stays a hair, a degree is a visible step. A scale drawn from this
    /// member's own spread would stretch to fill the track whatever it contained, which makes a
    /// steady wearer's quarter-degree wobble look exactly like another's full degree.
    /// </para>
    /// <para>
    /// The bounds are a skin range, not a body-temperature one. A wrist wearable does not measure
    /// core temperature (see <see cref="TemperatureComparison"/>), and running the axis up to a
    /// fever — or to the highest core temperature anyone has survived — would put every real
    /// reading in the left third of a track whose right half no wrist sensor can reach. Readings
    /// outside the axis clamp to its ends; the caption still states the true distance.
    /// </para>
    /// <para>
    /// Drawn only when there is a previous reading to fill to. A device's first night gets no bar
    /// rather than one marking today against nothing.
    /// </para>
    /// </remarks>
    private void ApplyTemperatureTrack(DashboardMetric metric)
    {
        if (metric.Value is not { } today || PreviousReading(metric) is not { } previous)
        {
            ProgressTrackBorder.IsVisible = false;
            MarkerGrid.IsVisible = false;
            OverflowFill.IsVisible = false;
            return;
        }

        const decimal floor = SkinTempAxisLow;
        const decimal scale = SkinTempAxisHigh - SkinTempAxisLow;

        ProgressTrackBorder.IsVisible = true;
        OverflowFill.IsVisible = false;
        // Its own colour, not activity's stacked pair: the same chrome filled to two different
        // kinds of thing (yesterday vs the extra there, a previous reading here) should not look
        // identical.
        ProgressFill.Background = new SolidColorBrush(
            (Color)Microsoft.Maui.Controls.Application.Current!.Resources["MetricTemperatureInk"]);
        SetProgress((double)((previous - floor) / scale));
        SetMarker((double)((today - floor) / scale));

        // The bar carries no text, and a screen reader has nothing to take from a fill width.
        SemanticProperties.SetDescription(
            ProgressTrackBorder, $"Today {today:0.#}°C, last reading {previous:0.#}°C");
    }

    /// <summary>
    /// What tonight's skin temperature is being read against, spelled out as the distance from it:
    /// "0.4°C above usual" rather than "vs. own nightly baseline", which named the comparison
    /// without ever making it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison is against this wearer's own nightly normal — the baseline the device itself
    /// reports — because there is no other honest one to draw. No standards body publishes a
    /// typical skin temperature to read 34°C against (see <c>HealthReferenceRanges</c>: a wrist
    /// wearable measures skin, not core, and the number moves with the room as much as with the
    /// wearer), so a "normal range" on this card would be our own figure wearing a publisher's
    /// authority.
    /// </para>
    /// <para>
    /// A device that has not sent a baseline still leaves one comparison available — the reading
    /// before this one — and the card makes it: which way the number moved is something a
    /// caregiver can read, where a bare 34°C is not. It is stated as a direction, not a verdict:
    /// the movement between two nights carries no judgement about either.
    /// </para>
    /// </remarks>
    private static string TemperatureComparison(DashboardMetric metric)
    {
        if (metric.Value is not { } value)
            return "Nightly reading";

        if (metric.Baseline is { } baseline)
        {
            var fromBaseline = value - baseline;
            // Below the resolution the caption itself prints — anything that would render as
            // "0°C above usual" is a night in line with their usual, and should say that instead.
            return Math.Abs(fromBaseline) < CaptionResolution
                ? "In line with usual"
                : $"{Math.Abs(fromBaseline):0.#}°C {(fromBaseline > 0 ? "above" : "below")} usual";
        }

        if (PreviousReading(metric) is not { } previous)
            return "Nightly reading";

        var change = value - previous;
        return Math.Abs(change) < CaptionResolution
            ? "Same as last reading"
            : $"{Math.Abs(change):0.#}°C {(change > 0 ? "up" : "down")} on last reading";
    }

    /// <summary>
    /// The reading before the latest one, skipping the days this member reported nothing for —
    /// the series carries a point per day whether or not it has a value, so "the day before" and
    /// "the reading before" are not the same question.
    /// </summary>
    private static decimal? PreviousReading(DashboardMetric metric)
    {
        var readings = metric.Series.Where(point => point.Value is not null).ToList();
        return readings.Count >= 2 ? readings[^2].Value : null;
    }

    public void ApplySpO2(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_spo2.svg";
        NameLabel.Text = "Blood Oxygen";
        SetReading(metric.Value is { } v ? $"{v:0.#}%" : "—", metric);

        // No baseline exists for this metric yet, so Status is always "unknown" and QualityScore
        // always null — pill and stars both stay hidden. A bare reading, not a judgement.
        ApplyStars(metric, matchPill: ApplyStatusPill(metric.Status));
        CaptionLabel.Text = "SpO2";
    }

    public void ApplyBreathingRate(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_breathing.svg";
        // "Breathing", not "Breathing Rate": the full name clips at this tile's half-grid
        // width, and the caption underneath already says "Breaths per minute" — the word "Rate"
        // was the only casualty worth taking.
        NameLabel.Text = "Breathing";
        SetReading(metric.Value is { } v ? $"{v:0.#} brpm" : "—", metric);

        // No baseline exists for this metric yet, same as SpO2 — a bare reading, not a trend.
        ApplyStars(metric, matchPill: ApplyStatusPill(metric.Status));
        CaptionLabel.Text = "Breaths per minute";
    }

    /// <summary>
    /// What the spoken form of a reading calls the thing it is being compared with, for the cards
    /// whose percentage is against the member's own baseline — which is all of them but Activity.
    /// </summary>
    private const string BaselineComparison = "usual";

    /// <summary>
    /// The card's reading, with how far it sits from this member's own normal on the same line —
    /// "73 bpm  ↑1%". Every card goes through here, so a metric that carries a comparison shows it
    /// in the same place and the same shape whichever tile it lands in.
    /// </summary>
    /// <returns>Whether a percentage actually went beside the reading. See <see cref="ApplyTrend"/>.</returns>
    private bool SetReading(string value, DashboardMetric metric) =>
        SetReading(value, metric.ChangePercent, BaselineComparison);

    /// <param name="comparedWith">
    /// What the percentage is measured against, as a screen reader should say it ("usual",
    /// "yesterday"). The arrow and the figure are the same either way; only the spoken form can
    /// say which comparison is being made, and the sighted reader has the caption for it.
    /// </param>
    /// <inheritdoc cref="SetReading(string, DashboardMetric)"/>
    private bool SetReading(string value, decimal? changePercent, string comparedWith)
    {
        ValueSpan.Text = value;
        return ApplyTrend(changePercent, comparedWith);
    }

    /// <summary>
    /// The movement beside the reading: an arrow for the direction and the distance from whatever
    /// the card compares against, green up and red down. See <see cref="Percent"/> for how far the
    /// figure is spelled out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It states the change itself ("↑63%"), not the reading as a share of normal ("163%"), which
    /// is what this used to print under the value with "of normal" beneath it as the only thing
    /// telling the two apart. On one line beside the reading there is no room for that caption,
    /// and an arrow in front of 163% would read as a number that had gone up by 163%.
    /// </para>
    /// <para>
    /// Direction alone decides the colour, so the same movement is the same colour on every card.
    /// The accessory below still carries the verdict — the pill and the star row read the size of
    /// the deviation against this member's own bands, and either can disagree with the arrow's
    /// sentiment on a metric where up is not good news.
    /// </para>
    /// <para>
    /// Nothing is drawn where the caller has no comparison to hand: no baseline for the metric
    /// (SpO2 and breathing rate have none yet), no previous day for Activity to measure against —
    /// including a previous day of zero, which no percentage can express — or a movement under
    /// <see cref="TrendResolution"/>, which the label could only print as an arrow over "0%".
    /// </para>
    /// </remarks>
    private bool ApplyTrend(decimal? changePercent, string comparedWith)
    {
        if (changePercent is not { } change || Math.Abs(change) < TrendResolution)
        {
            TrendSpan.Text = string.Empty;
            SemanticProperties.SetDescription(ReadingLabel, ValueSpan.Text);
            return false;
        }

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var up = change > 0;
        var size = Math.Abs(change);

        // Two spaces rather than one: the percentage is set five points smaller than the reading
        // it follows, and a single gap at that difference reads as part of the same figure.
        TrendSpan.Text = $"  {(up ? "↑" : "↓")}{Percent(size)}";
        TrendSpan.TextColor = (Color)resources[up ? "StatusGreen" : "StatusRed"];

        // The arrow is a glyph a screen reader either skips or names as punctuation, and the two
        // spans are read as one run — so the line gets a spoken form that says the comparison.
        SemanticProperties.SetDescription(
            ReadingLabel, $"{ValueSpan.Text}, {Percent(size)} {(up ? "above" : "below")} {comparedWith}");
        return true;
    }

    /// <summary>
    /// A movement as a percentage, to the decimal it needs and no further: whole percent from one
    /// up, a tenth below that.
    /// </summary>
    /// <remarks>
    /// Skin temperature is why the tenth exists. A night 0.1°C off a 33.8°C baseline is 0.3% — a
    /// real movement the card's own caption states in degrees, and one a whole-percent label could
    /// only round to nothing and drop. Everything a wearable reports in a unit with a distant zero
    /// moves in fractions of a percent like this; the metrics that move in whole numbers never see
    /// the decimal, so no tile pays for it in width.
    /// </remarks>
    private static string Percent(decimal size) => size < 1m ? $"{size:0.#}%" : $"{size:0}%";

    /// <summary>
    /// Heart rate's accessory. The tint, ink and wording come from <see cref="MetricStatus"/>, which
    /// the Member Detail screen's trend cards read too, so one status can never be described two ways.
    /// </summary>
    /// <returns>
    /// Whether a pill actually went on the card — which is what decides where the star row below it
    /// takes its colour from. See <see cref="ApplyStars"/>.
    /// </returns>
    private bool ApplyStatusPill(string status) => ShowPill(MetricStatus.Pill(status));

    /// <summary>
    /// Renders the pill row, or hides it for a reading that earned none. One renderer for both
    /// pill vocabularies — the status comparison (<see cref="MetricStatus.Pill"/>) and sleep's
    /// rating band (<see cref="MetricStatus.SleepQualityPill"/>) — so the two can never drift
    /// in chrome, only in wording.
    /// </summary>
    private bool ShowPill((string Tint, string Ink, string Text)? pill)
    {
        if (pill is not { } p)
        {
            StatusPillBorder.IsVisible = false;
            return false;
        }

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        StatusPillBorder.BackgroundColor = (Color)resources[p.Tint];
        StatusPillLabel.TextColor = (Color)resources[p.Ink];
        StatusPillLabel.Text = p.Text;
        StatusPillBorder.IsVisible = true;
        return true;
    }

    /// <summary>
    /// Every card's rating of its reading against the member's own normal, out of five (the API's
    /// <see cref="DashboardMetric.QualityScore"/>). Hidden entirely when the metric has no
    /// comparison to make, so an unrated card shows no row rather than an empty one.
    /// </summary>
    /// <param name="matchPill">
    /// Whether this card is showing a pill built from the metric's status, from
    /// <see cref="ApplyStatusPill"/>. When it is, the stars take the pill's colour and nothing
    /// else — two accents disagreeing an inch apart on one card is worse than either being
    /// imprecise. When it is not, they colour themselves from the rating (see
    /// <see cref="StarInk"/>) — including on sleep, whose pill is itself named from the star
    /// bands, so the accents agree there without either reading the status.
    /// </param>
    /// <remarks>
    /// Earned stars are filled in colour, the rest left as an outline. The row used to be five
    /// copies of one grey glyph separated only by opacity, which at 15dp left a four-star reading
    /// looking much like a one-star one — most visibly on skin temperature, where the coloured
    /// NORMAL pill sits directly above a row of grey.
    /// </remarks>
    private void ApplyStars(DashboardMetric metric, bool matchPill)
    {
        if (metric.QualityScore is not { } score)
        {
            StarRow.IsVisible = false;
            AutomationProperties.SetIsInAccessibleTree(StarRow, false);
            return;
        }

        var filled = Math.Clamp(score, 0, StarCount);
        StarRow.Render(filled, matchPill ? MetricStatus.Accent(metric.Status) : StarInk(filled));

        // One value, so one accessibility stop: the stars differ only by fill, which no screen
        // reader conveys.
        SemanticProperties.SetDescription(StarRow, $"{NameLabel.Text}: {filled} out of {StarCount}");
        AutomationProperties.SetIsInAccessibleTree(StarRow, true);
        StarRow.IsVisible = true;
    }

    /// <summary>
    /// The fill for the earned stars on the two cards whose accent comes from the rating itself —
    /// activity, which shows no pill, and sleep, whose pill names these same bands — read off
    /// <c>RateAgainstNormal</c>'s own scale: 3-5 green, 2 yellow, 1 orange.
    /// </summary>
    /// <remarks>
    /// Only those two, because only there is the rating the sole thing on the card with a colour.
    /// This used to be applied by asking whether the status had a pill mapping rather than whether
    /// this card had put one on screen, which is a different question and got sleep wrong:
    /// sleep has a status, so it took the status colour, and a habitually short sleeper's two stars
    /// came out green — the one reading the rating exists to surface, painted as if it were fine.
    /// Deriving the colour everywhere instead would only move the error to skin temperature, whose
    /// bands do not nest in its status thresholds (3 stars is 1-1.5σ, which is yellow).
    /// </remarks>
    private static Color StarInk(int filled) =>
        MetricStatus.Accent(filled switch
        {
            >= 3 => "green",
            2 => "yellow",
            _ => "orange",
        });

    /// <summary>
    /// Fills the track with one segment (temperature, or activity still behind yesterday).
    /// </summary>
    private void SetProgress(double fraction)
    {
        // No clamp here: StackedTrack keeps the width inside 0–1 for both callers.
        SetStack(fraction, 0);
        OverflowFill.IsVisible = false;
        ProgressFill.StrokeShape = new RoundRectangle { CornerRadius = 12 };
    }

    /// <summary>
    /// Stacks yesterday's share and today's extra as two star columns, with the empty remainder
    /// in a third. A platform ProgressBar cannot do this — it is one fill — which is why the
    /// track is already a Grid.
    /// </summary>
    private void SetStack(double compared, double overflow)
    {
        // The widths are worked out in StackedTrack, which guarantees all three are non-negative
        // numbers — GridLength throws on anything else, and a throw here strands the dashboard.
        var track = StackedTrack.For(compared, overflow);

        ProgressGrid.ColumnDefinitions[0].Width = new GridLength(track.Compared, GridUnitType.Star);
        ProgressGrid.ColumnDefinitions[1].Width = new GridLength(track.Overflow, GridUnitType.Star);
        ProgressGrid.ColumnDefinitions[2].Width = new GridLength(track.Remainder, GridUnitType.Star);

        OverflowFill.IsVisible = track.IsStacked;
        if (!track.IsStacked)
        {
            ProgressFill.StrokeShape = new RoundRectangle { CornerRadius = 12 };
            return;
        }

        // Yesterday was zero: the extra is the whole bar, so it wears both rounded ends.
        OverflowFill.StrokeShape = track.Compared <= 0
            ? new RoundRectangle { CornerRadius = 12 }
            : new RoundRectangle { CornerRadius = new CornerRadius(0, 12, 12, 0) };
        ProgressFill.StrokeShape = track.Compared <= 0
            ? new RoundRectangle { CornerRadius = 12 }
            : new RoundRectangle { CornerRadius = new CornerRadius(12, 0, 0, 12) };
    }

    /// <summary>
    /// Places the tick at <paramref name="fraction"/> along the track, the same way
    /// <see cref="SetProgress"/> fills it — two star columns either side of an auto-width mark.
    /// </summary>
    private void SetMarker(double fraction)
    {
        var at = Math.Clamp(fraction, 0, 1);
        MarkerGrid.ColumnDefinitions[0].Width = new GridLength(at, GridUnitType.Star);
        MarkerGrid.ColumnDefinitions[2].Width = new GridLength(1 - at, GridUnitType.Star);
        MarkerGrid.IsVisible = true;
    }
}
