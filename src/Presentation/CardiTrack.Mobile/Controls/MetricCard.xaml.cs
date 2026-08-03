using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Controls;

public partial class MetricCard : ContentView
{
    public MetricCard()
    {
        InitializeComponent();
    }

    public void ApplySteps(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_steps.svg";
        Sparkline.LineColor = (Color)Microsoft.Maui.Controls.Application.Current!.Resources["Primary"];
        ValueLabel.Text = metric.Value is { } v ? $"{v:N0} steps" : "—";

        if (metric is { Value: { } value, Goal: > 0 })
        {
            GoalProgress.IsVisible = true;
            GoalProgress.Progress = Math.Min(1.0, (double)(value / metric.Goal.Value));
            SubtitleLabel.Text = $"Goal {metric.Goal:N0}";
        }
        else
        {
            GoalProgress.IsVisible = false;
            SubtitleLabel.Text = "Daily activity";
        }

        ApplyComparison(metric, higherIsBetter: true);
        Sparkline.Points = metric.Series.Select(p => p.Value).ToList();
    }

    public void ApplyHeartRate(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_heart.svg";
        Sparkline.LineColor = (Color)Microsoft.Maui.Controls.Application.Current!.Resources["StatusRed"];
        ValueLabel.Text = metric.Value is { } v ? $"{v:N0} bpm" : "—";
        SubtitleLabel.Text = metric is { RangeLow: { } low, RangeHigh: { } high }
            ? $"{low}-{high} bpm typical"
            : "Resting heart rate";

        ApplyComparison(metric, higherIsBetter: false);
        Sparkline.Points = metric.Series.Select(p => p.Value).ToList();
    }

    public void ApplySleep(DashboardMetric metric)
    {
        MetricIcon.Source = "icon_metric_sleep.svg";
        Sparkline.LineColor = (Color)Microsoft.Maui.Controls.Application.Current!.Resources["SleepPurple"];
        ValueLabel.Text = metric.Value is { } v ? $"{v:0.#} hours" : "—";
        SubtitleLabel.Text = metric.QualityScore is { } stars
            ? string.Concat(Enumerable.Repeat("★", stars)) + string.Concat(Enumerable.Repeat("☆", 5 - stars))
            : "Sleep";

        ApplyComparison(metric, higherIsBetter: true);
        Sparkline.Points = metric.Series.Select(p => p.Value).ToList();
    }

    private void ApplyComparison(DashboardMetric metric, bool higherIsBetter)
    {
        if (metric.ChangePercent is not { } change)
        {
            ComparisonLabel.IsVisible = false;
            return;
        }

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var arrow = change >= 0 ? "▲" : "▼";
        var isGood = higherIsBetter ? change >= 0 : change <= 0;
        var percentOfNormal = 100 + change;

        ComparisonLabel.Text = $"{arrow} {percentOfNormal:0}% of normal";
        ComparisonLabel.TextColor = Math.Abs(change) <= 30
            ? (Color)resources["StatusGreen"]
            : isGood
                ? (Color)resources["StatusGreen"]
                : (Color)resources["StatusOrange"];
        ComparisonLabel.IsVisible = true;
    }
}
