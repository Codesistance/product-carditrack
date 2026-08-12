using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Controls;

public partial class MetricCard : ContentView
{
    private const int StarCount = 5;
    private const double DimmedStarOpacity = 0.25;

    /// <summary>
    /// Closes every explanation. CardiTrack is not a diagnostic tool, and a popup that has just
    /// spent a paragraph describing clinical reference ranges is exactly where that has to be said
    /// rather than assumed.
    /// </summary>
    private const string NotADiagnosis =
        "CardiTrack watches for changes from someone's own normal. It doesn't diagnose.";

    private string _explanationTitle = string.Empty;
    private string _explanation = string.Empty;

    /// <summary>
    /// Raised when the tile's "i" is tapped, carrying the copy to show. The card does not open the
    /// popup itself: <see cref="Services.IPopupService"/> belongs to the page, and a control that
    /// reached for it would be a second route into the app's dialogs from inside a grid cell.
    /// </summary>
    public event EventHandler<MetricExplanation>? ExplanationRequested;

    public MetricCard()
    {
        InitializeComponent();

        // The same swatch the Member Detail trend cards key their charts with, so the dashed rule
        // is introduced identically on both screens.
        BaselineLegend.Insert(0, new TrendLegendSwatch(TrendLegendMark.Baseline));
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

        // No published range: nobody publishes a daily step count, and converting WHO's
        // activity-in-minutes guidance into steps would be our arithmetic wearing WHO's name.
        ApplyComparison(
            metric,
            "{0:N0}",
            "steps",
            "Dashes: their usual day.",
            "The dashed line is this CardiMember's own usual step count, learned from their own "
            + "history — it is not a target. The bar above tracks their daily goal, which is a "
            + "separate thing. No health body publishes a step count to compare anyone against, so "
            + "this card shows no shaded band. A quieter day than usual is worth noticing, not "
            + "worrying about.");
    }

    public void ApplyHeartRate(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_heart.svg";
        NameLabel.Text = "Heart Rate";
        ValueLabel.Text = metric.Value is { } v ? $"{v:N0} bpm" : "—";

        ApplyStatusPill(metric.Status);
        ApplyStars(metric.QualityScore);
        CaptionLabel.Text = "Resting heart rate";

        ApplyComparison(
            metric,
            "{0:N0}",
            "bpm",
            $"Dashes: their own normal.{Band(metric, "{0:N0}")}",
            "The dashed line is this CardiMember's own resting heart rate, learned over time. The "
            + $"shaded band is the typical adult range{Published(metric, "{0:N0}", "bpm")}. Their "
            + "own normal is the more useful of the two: a resting rate that is steady for them can "
            + "sit outside the published band and be perfectly ordinary for them.");
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

        ApplyComparison(
            metric,
            "{0:0.#}",
            "h",
            $"Dashes: their usual night.{Band(metric, "{0:0.#}")}",
            "The dashed line is this CardiMember's own usual night. The shaded band is the nightly "
            + $"sleep recommended for their age group{Published(metric, "{0:0.#}", "hours")} — the "
            + "recommendation drops by an hour from age 65, so it is drawn for their age rather "
            + "than a single adult figure. This measures how long they slept, not how well.");
    }

    public void ApplyTemperature(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_temperature.svg";
        NameLabel.Text = "Skin Temp";
        ValueLabel.Text = metric.Value is { } v ? $"{v:0.#}°C" : "—";

        ApplyStatusPill(metric.Status);
        ApplyStars(metric.QualityScore);
        CaptionLabel.Text = "Nightly reading";

        // No published range on purpose: skin temperature is a wearer-relative measurement with no
        // population normal, which is also why the wording below leads with what it is not.
        ApplyComparison(
            metric,
            "{0:0.#}",
            "°C",
            "Dashes: their own nightly normal.",
            "A wrist wearable measures skin temperature, not core body temperature — this is not a "
            + "fever reading. The dashed line is the device's own nightly baseline for this "
            + "CardiMember, and what carries meaning is the distance from it rather than the number "
            + "itself. There is no published range to shade behind it, because there is no "
            + "population normal for a measurement this personal.");
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

        ApplyComparison(
            metric,
            "{0:0.#}",
            "%",
            $"{Band(metric, "{0:0.#}", lead: true)} No personal normal yet.",
            "The shaded band is the normal blood oxygen range"
            + $"{Published(metric, "{0:0.#}", "%")}. CardiTrack has not learned a personal normal "
            + "for blood oxygen, so this card has no dashed line — the reading is shown against the "
            + "published range alone.");
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

        ApplyComparison(
            metric,
            "{0:0.#}",
            "brpm",
            $"{Band(metric, "{0:0.#}", lead: true)} No personal normal yet.",
            "The shaded band is the normal adult breathing rate"
            + $"{Published(metric, "{0:0.#}", "breaths per minute")}. CardiTrack has not learned a "
            + "personal normal for breathing rate, so this card has no dashed line.");
    }

    /// <summary>
    /// The bottom half of every tile: the gauge, the key naming the dashed rule on it, the footer
    /// line, and the fuller copy behind the "i".
    /// </summary>
    /// <param name="baselineFormat">How the baseline figure is written in the key.</param>
    /// <param name="unit">The unit that follows it — short, because the key shares a half-width tile.</param>
    private void ApplyComparison(
        DashboardMetric metric, string baselineFormat, string unit, string footer, string explanation)
    {
        BaselineBlock.IsVisible = Strip.Apply(metric, MetricStatus.Accent(metric.Status));

        // The key names the dashed rule only. The band names itself in the footer, where there is
        // room to attribute it to whoever publishes it — an unattributed range on a health screen
        // is a number with nobody standing behind it.
        BaselineLegend.IsVisible = metric.Baseline is not null;
        if (metric.Baseline is { } baseline)
            BaselineKeyLabel.Text = $"Baseline {string.Format(baselineFormat, baseline)} {unit}";

        SemanticProperties.SetDescription(Strip, StripDescription(metric, baselineFormat, unit));

        // Trimmed because the band clause is empty for a metric with no published range, which for
        // the two cards that lead with it would otherwise open the line with a space.
        FooterLabel.Text = footer.Trim();
        _explanationTitle = NameLabel.Text;
        _explanation = $"{explanation}\n\n{NotADiagnosis}";
    }

    /// <summary>
    /// The band as the footer says it — leading space included so a metric with no published range
    /// contributes nothing rather than a dangling separator.
    /// </summary>
    /// <param name="lead">Whether this opens the footer, in which case it drops the leading space.</param>
    private static string Band(DashboardMetric metric, string format, bool lead = false) =>
        metric.Reference is { } reference
            ? $"{(lead ? string.Empty : " ")}Band "
              + $"{string.Format(format, reference.Low)}–{string.Format(format, reference.High)} "
              + $"({reference.Source})."
            : string.Empty;

    /// <summary>The same range as the popup says it, spelled out with its unit and publisher.</summary>
    private static string Published(DashboardMetric metric, string format, string unit) =>
        metric.Reference is { } reference
            ? $" published by {reference.Source} ({string.Format(format, reference.Low)}–"
              + $"{string.Format(format, reference.High)} {unit})"
            : string.Empty;

    /// <summary>
    /// What the gauge says out loud. A <see cref="GraphicsView"/> conveys nothing on its own, so
    /// without this the one mark that matters most on the tile is invisible to a screen reader.
    /// </summary>
    private static string StripDescription(DashboardMetric metric, string format, string unit)
    {
        var parts = new List<string>();
        if (metric.Value is { } value)
            parts.Add($"{string.Format(format, value)} {unit}");
        if (metric.Baseline is { } baseline)
            parts.Add($"their own baseline {string.Format(format, baseline)} {unit}");
        if (metric.Reference is { } reference)
            parts.Add($"typical {string.Format(format, reference.Low)} to "
                      + $"{string.Format(format, reference.High)} {unit} per {reference.Source}");

        return string.Join(", ", parts);
    }

    private void OnExplainTapped(object? sender, TappedEventArgs e) =>
        ExplanationRequested?.Invoke(this, new MetricExplanation(_explanationTitle, _explanation));

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
    private void ApplyStatusPill(string status)
    {
        if (MetricStatus.Pill(status) is not { } pill)
        {
            StatusPillBorder.IsVisible = false;
            return;
        }

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        StatusPillBorder.BackgroundColor = (Color)resources[pill.Tint];
        StatusPillLabel.TextColor = (Color)resources[pill.Ink];
        StatusPillLabel.Text = pill.Text;
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

/// <summary>What a tile's "i" asks the page to show.</summary>
/// <param name="Title">The metric's name, so the popup says which card it belongs to.</param>
public sealed record MetricExplanation(string Title, string Message);
