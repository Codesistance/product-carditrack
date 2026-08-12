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
        string iconSource, string name, string valueFormat, string axisFormat, DashboardMetric metric, int days)
    {
        IconSource = iconSource;
        Name = name;
        AxisFormat = axisFormat;
        Metric = metric;
        ValueText = metric.Value is { } value ? string.Format(valueFormat, value) : "—";
        _days = days;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string IconSource { get; }
    public string Name { get; }

    /// <summary>The latest reading, already formatted with its unit ("72 bpm").</summary>
    public string ValueText { get; }

    /// <summary>Format for the chart's own min/max labels — the same number without its unit.</summary>
    public string AxisFormat { get; }

    public DashboardMetric Metric { get; }

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
