using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Controls;

public partial class MetricCard : ContentView
{
    private const int StarCount = 5;
    private const double DimmedStarOpacity = 0.25;

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
        ApplyStars(metric.QualityScore);

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

        ApplyStatusPill(metric.Status);
        ApplyStars(metric.QualityScore);
        CaptionLabel.Text = metric is { RangeLow: { } low, RangeHigh: { } high }
            ? $"{low}-{high} bpm typical"
            : "Resting heart rate";
    }

    public void ApplySleep(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_sleep.svg";
        NameLabel.Text = "Sleep";
        ValueLabel.Text = metric.Value is { } v ? $"{v:0.#} hours" : "—";

        ApplyStars(metric.QualityScore);
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

        ApplyStatusPill(metric.Status);
        ApplyStars(metric.QualityScore);
        CaptionLabel.Text = metric.Baseline is not null ? "vs. own nightly baseline" : "Nightly reading";
    }

    public void ApplySpO2(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_spo2.svg";
        NameLabel.Text = "Blood Oxygen";
        ValueLabel.Text = metric.Value is { } v ? $"{v:0.#}%" : "—";

        // No baseline exists for this metric yet, so Status is always "unknown" and QualityScore
        // always null — pill and stars both stay hidden. A bare reading, not a judgement.
        ApplyStatusPill(metric.Status);
        ApplyStars(metric.QualityScore);
        CaptionLabel.Text = "SpO2";
    }

    public void ApplyBreathingRate(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_breathing.svg";
        NameLabel.Text = "Breathing Rate";
        ValueLabel.Text = metric.Value is { } v ? $"{v:0.#} brpm" : "—";

        // No baseline exists for this metric yet, same as SpO2 — a bare reading, not a trend.
        ApplyStatusPill(metric.Status);
        ApplyStars(metric.QualityScore);
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
    /// Heart rate's accessory. The wording is deliberately non-clinical — CardiTrack is not a
    /// medical device, so the pill reports how the reading compares with this member's own
    /// baseline rather than naming it high or low.
    /// </summary>
    private void ApplyStatusPill(string status)
    {
        var (tint, ink, text) = status switch
        {
            "green" => ("PillGreenBackground", "StatusGreen", "NORMAL"),
            "yellow" => ("PillYellowBackground", "StatusYellow", "UNUSUAL"),
            "orange" => ("PillOrangeBackground", "StatusOrange", "CHECK IN"),
            "red" => ("PillRedBackground", "StatusRed", "URGENT"),
            _ => (null, null, null),
        };

        if (tint is null)
        {
            StatusPillBorder.IsVisible = false;
            return;
        }

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        StatusPillBorder.BackgroundColor = (Color)resources[tint];
        StatusPillLabel.TextColor = (Color)resources[ink!];
        StatusPillLabel.Text = text;
        StatusPillBorder.IsVisible = true;
    }

    /// <summary>
    /// Every card's rating of its reading against the member's own normal, out of five (the API's
    /// <see cref="DashboardMetric.QualityScore"/>). Hidden entirely when the metric has no
    /// comparison to make, so an unrated card shows no row rather than an empty one. Unearned
    /// stars are dimmed rather than swapped for an outline asset — the icon set is hand-authored
    /// and has no outline star yet.
    /// </summary>
    private void ApplyStars(int? qualityScore)
    {
        StarRow.Clear();
        if (qualityScore is not { } score)
        {
            StarRow.IsVisible = false;
            AutomationProperties.SetIsInAccessibleTree(StarRow, false);
            return;
        }

        var filled = Math.Clamp(score, 0, StarCount);
        for (var i = 0; i < StarCount; i++)
        {
            var star = new Image
            {
                Source = "icon_star.svg",
                WidthRequest = 15,
                HeightRequest = 15,
                Opacity = i < filled ? 1 : DimmedStarOpacity,
            };
            // Individually meaningless: the stars differ only by opacity, which no screen reader
            // conveys, so five identical "image" stops would be walked for one value. The row
            // below speaks for all of them.
            AutomationProperties.SetIsInAccessibleTree(star, false);
            StarRow.Add(star);
        }

        SemanticProperties.SetDescription(StarRow, $"{NameLabel.Text}: {filled} out of {StarCount}");
        AutomationProperties.SetIsInAccessibleTree(StarRow, true);
        StarRow.IsVisible = true;
    }

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
