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
        // and how long the night was, the status reads its duration against the baseline — so it
        // shows no pill, and the star row is free to colour itself.
        ApplyStars(metric, matchPill: false);
        CaptionLabel.Text = metric.ChangePercent switch
        {
            > 0 => "Better than average",
            < 0 => "Less than usual",
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
    /// Activity's accessory: how today compares with the member's own baseline.
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
        TrendLabel.TextColor = Math.Abs(change) <= 30 || isGood
            ? (Color)resources["StatusGreen"]
            : (Color)resources["StatusOrange"];
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
    private bool ApplyStatusPill(string status)
    {
        if (MetricStatus.Pill(status) is not { } pill)
        {
            StatusPillBorder.IsVisible = false;
            return false;
        }

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        StatusPillBorder.BackgroundColor = (Color)resources[pill.Tint];
        StatusPillLabel.TextColor = (Color)resources[pill.Ink];
        StatusPillLabel.Text = pill.Text;
        StatusPillBorder.IsVisible = true;
        return true;
    }

    /// <summary>
    /// Every card's rating of its reading against the member's own normal, out of five (the API's
    /// <see cref="DashboardMetric.QualityScore"/>). Hidden entirely when the metric has no
    /// comparison to make, so an unrated card shows no row rather than an empty one.
    /// </summary>
    /// <param name="matchPill">
    /// Whether this card is showing a status pill, from <see cref="ApplyStatusPill"/>. When it is,
    /// the stars take the pill's colour and nothing else — two accents disagreeing an inch apart on
    /// one card is worse than either being imprecise. When it is not, they colour themselves from
    /// the rating; see <see cref="StarInk"/>.
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
    /// The fill for the earned stars on the two cards that show no pill — activity and sleep — read
    /// off the rating itself on <c>RateAgainstNormal</c>'s own bands: 3-5 green, 2 yellow, 1 orange.
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
