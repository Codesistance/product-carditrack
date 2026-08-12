using System.ComponentModel;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// One metric's slide in the Member Detail screen's Key Metric Trends carousel: everything a
/// <see cref="MetricTrendCard"/> needs to draw itself, plus the caregiver's chosen window.
/// </summary>
/// <remarks>
/// The window lives on the item rather than on the card so the whole carousel retunes by setting it
/// once per item: the cards MAUI has realised redraw in place, and the ones it recycles later read
/// the new value on the way in. Rebuilding the carousel's ItemsSource instead would snap it back to
/// the first metric every time the caregiver changed windows.
/// </remarks>
public sealed class MetricTrend : INotifyPropertyChanged
{
    private int _days;

    public MetricTrend(
        string iconSource, string inkKey, string name, string valueFormat, string axisFormat,
        DashboardMetric metric, int days)
    {
        IconSource = iconSource;
        InkKey = inkKey;
        Name = name;
        AxisFormat = axisFormat;
        Metric = metric;
        ValueText = metric.Value is { } value ? string.Format(valueFormat, value) : "—";
        _days = days;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string IconSource { get; }

    /// <summary>
    /// Resource key for this metric's own colour — the stroke of the glyph in
    /// <see cref="IconSource"/>. The chart draws its line in it, so the line and the icon above it
    /// are one colour rather than two that happen to share a card.
    /// </summary>
    public string InkKey { get; }

    public string Name { get; }

    /// <summary>The latest reading, already formatted with its unit ("72 bpm").</summary>
    public string ValueText { get; }

    /// <summary>Format for the chart's own min/max labels — the same number without its unit.</summary>
    public string AxisFormat { get; }

    public DashboardMetric Metric { get; }

    /// <summary>
    /// The legend entry for this member's own learned normal, or null for a metric that has none
    /// — SpO2 and breathing rate always, and any metric while the baseline is still learning.
    /// </summary>
    /// <remarks>
    /// Unitless, like the chart's own min/max labels: the unit is already on the headline reading
    /// a few dp above, and a legend is read as an annotation of the axis it sits under.
    /// </remarks>
    public string? BaselineText =>
        Metric.Baseline is { } baseline ? $"Baseline {string.Format(AxisFormat, baseline)}" : null;

    /// <summary>
    /// The legend entry for the published typical-adult range, attributed to whoever publishes it,
    /// or null for a metric no standards body publishes one for.
    /// </summary>
    public string? ReferenceText =>
        Metric.Reference is { } reference
            ? $"Typical {string.Format(AxisFormat, reference.Low)}–{string.Format(AxisFormat, reference.High)}"
                + $" ({reference.Source})"
            : null;

    /// <summary>How many days of the series the card shows; one of <see cref="TrendWindowSelector.Windows"/>.</summary>
    public int Days
    {
        get => _days;
        set
        {
            if (_days == value)
                return;
            _days = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Days)));
        }
    }

    /// <summary>
    /// The tail of the series the current window covers, oldest first. The API always sends the
    /// widest window, so narrowing is a slice rather than another round trip; a member whose series
    /// is shorter than the window asked for simply shows everything it has.
    /// </summary>
    public IReadOnlyList<MetricPoint> Window =>
        Metric.Series.Count <= _days
            ? Metric.Series
            : Metric.Series.Skip(Metric.Series.Count - _days).ToList();
}
