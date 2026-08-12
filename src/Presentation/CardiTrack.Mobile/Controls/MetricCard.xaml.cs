using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Controls;

public partial class MetricCard : ContentView
{
    private const int StarCount = StarRatingView.StarCount;

    public MetricCard()
    {
        InitializeComponent();
    }

    public void ApplySteps(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_steps.svg";
        NameLabel.Text = "Activity";
        ValueLabel.Text = metric.Value is { } v ? $"{v:N0} steps" : "—";

        ApplyTrend(metric, higherIsBetter: true);
        // This card carries a trend arrow rather than a pill, so there is nothing on it for the
        // star row to match and the rating colours itself.
        ApplyStars(metric, matchPill: false);

        if (metric is { Value: { } value, Goal: > 0 })
        {
            ProgressTrackBorder.IsVisible = true;
            SetProgress((double)(value / metric.Goal!.Value));
            if (!TrendLabel.IsVisible)
                CaptionLabel.Text = $"Goal {metric.Goal:N0}";
        }
        else
        {
            ProgressTrackBorder.IsVisible = false;
            if (!TrendLabel.IsVisible)
                CaptionLabel.Text = "Daily activity";
        }
    }

    public void ApplyHeartRate(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_heart.svg";
        NameLabel.Text = "Heart Rate";
        ValueLabel.Text = metric.Value is { } v ? $"{v:N0} bpm" : "—";

        ApplyStars(metric, matchPill: ApplyStatusPill(metric.Status));
        CaptionLabel.Text = metric is { RangeLow: { } low, RangeHigh: { } high }
            ? $"{low}-{high} bpm typical"
            : "Resting heart rate";
    }

    public void ApplySleep(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_sleep.svg";
        NameLabel.Text = "Sleep";
        ValueLabel.Text = metric.Value is { } v ? $"{v:0.#} hours" : "—";

        // The one card whose stars and status answer different questions — the stars read how well
        // and how long the night was, the status reads its duration against the baseline — so its
        // pill names the band of the rating itself rather than the status, and the star row keeps
        // colouring itself from the same bands: the two accents agree by construction, without
        // either reading the status that may honestly disagree with both.
        ShowPill(MetricStatus.SleepQualityPill(metric.QualityScore));
        ApplyStars(metric, matchPill: false);
        // Direction only, no verdict — a longer night is not automatically a better one, and this
        // caption used to call twelve hours "Better than average" directly under the stars that
        // now mark it down for exactly that. The rating carries the judgement; this says which way
        // the night went.
        CaptionLabel.Text = metric.ChangePercent switch
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
        ValueLabel.Text = metric.Value is { } v ? $"{v:0.#}°C" : "—";

        ApplyStars(metric, matchPill: ApplyStatusPill(metric.Status));
        CaptionLabel.Text = metric.Baseline is not null ? "vs. own nightly baseline" : "Nightly reading";
    }

    public void ApplySpO2(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_spo2.svg";
        NameLabel.Text = "Blood Oxygen";
        ValueLabel.Text = metric.Value is { } v ? $"{v:0.#}%" : "—";

        // No baseline exists for this metric yet, so Status is always "unknown" and QualityScore
        // always null — pill and stars both stay hidden. A bare reading, not a judgement.
        ApplyStars(metric, matchPill: ApplyStatusPill(metric.Status));
        CaptionLabel.Text = "SpO2";
    }

    public void ApplyBreathingRate(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_breathing.svg";
        NameLabel.Text = "Breathing Rate";
        ValueLabel.Text = metric.Value is { } v ? $"{v:0.#} brpm" : "—";

        // No baseline exists for this metric yet, same as SpO2 — a bare reading, not a trend.
        ApplyStars(metric, matchPill: ApplyStatusPill(metric.Status));
        CaptionLabel.Text = "Breaths per minute";
    }

    /// <summary>
    /// Activity's accessory: how today compares with the member's own baseline. Its colour bands
    /// are the status thresholds — the same 30%/50% lines the star bands nest inside — so the
    /// arrow and the star row beneath it can never accent one reading two ways: a 30-50%
    /// shortfall is yellow on both, not orange over a yellow two-star row.
    /// </summary>
    private void ApplyTrend(DashboardMetric metric, bool higherIsBetter)
    {
        if (metric.ChangePercent is not { } change)
        {
            TrendLabel.IsVisible = false;
            return;
        }

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var isGood = higherIsBetter ? change >= 0 : change <= 0;

        TrendLabel.Text = $"{(change >= 0 ? "↗" : "↘")} {100 + change:0}%";
        TrendLabel.TextColor = (Color)resources[isGood || Math.Abs(change) <= 30
            ? "StatusGreen"
            : Math.Abs(change) <= 50 ? "StatusYellow" : "StatusOrange"];
        TrendLabel.IsVisible = true;
        CaptionLabel.Text = "of normal";
    }

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
    /// Fills the track proportionally with two star columns, which avoids having to measure the
    /// track — a ProgressBar can't carry the design's gradient fill.
    /// </summary>
    private void SetProgress(double fraction)
    {
        var filled = Math.Clamp(fraction, 0, 1);
        ProgressGrid.ColumnDefinitions[0].Width = new GridLength(filled, GridUnitType.Star);
        ProgressGrid.ColumnDefinitions[1].Width = new GridLength(1 - filled, GridUnitType.Star);
    }
}
