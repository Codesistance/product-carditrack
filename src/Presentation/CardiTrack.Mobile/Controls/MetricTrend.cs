using System.ComponentModel;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Charts;

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
    private readonly string _valueFormat;

    private int _days;
    private DashboardMetric _metric;

    public MetricTrend(
        string iconSource, string inkKey, string name, string valueFormat, string axisFormat,
        DashboardMetric metric, int days, string? memberFirstName = null)
    {
        IconSource = iconSource;
        InkKey = inkKey;
        Name = name;
        AxisFormat = axisFormat;
        MemberFirstName = memberFirstName;
        _valueFormat = valueFormat;
        _metric = metric;
        ValueText = Format(metric);
        _days = days;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Whose readings these are. Carried on the item because the card is realised inside a
    /// <c>DataTemplate</c> and has no page to ask — see <see cref="MetricTrendCard"/>, which needs
    /// it to open the full-screen view of this same metric.
    /// </summary>
    public Guid MemberId { get; set; }

    public string IconSource { get; }

    /// <summary>
    /// Resource key for this metric's own colour — the stroke of the glyph in
    /// <see cref="IconSource"/>. The chart draws its line in it, so the line and the icon above it
    /// are one colour rather than two that happen to share a card.
    /// </summary>
    public string InkKey { get; }

    public string Name { get; }

    /// <summary>The latest reading, already formatted with its unit ("72 bpm").</summary>
    public string ValueText { get; private set; }

    /// <summary>Format for the chart's own min/max labels — the same number without its unit.</summary>
    public string AxisFormat { get; }

    /// <summary>
    /// This metric's current readings. Settable for the same reason <see cref="Days"/> is: a
    /// refresh that brings new numbers for the metrics already on screen retunes the items the
    /// carousel is holding, and the realised cards redraw in place. Replacing the carousel's
    /// <c>ItemsSource</c> instead would snap it back to the first metric, which on this screen
    /// means a caregiver reading the sleep chart is put back on activity by a background tick.
    /// </summary>
    /// <remarks>
    /// Raised as an all-properties change (the empty property name every binder reads as "re-read
    /// everything") rather than as <c>Metric</c> alone, because half this class is derived from it
    /// — <see cref="ValueText"/>, <see cref="BaselineText"/>, <see cref="ReferenceText"/> and
    /// <see cref="Window"/> all move when it does, and a binding on any of them would otherwise
    /// never hear about it. Naming the four separately would be the same information at the cost
    /// of four extra <c>Render</c> passes per card per refresh, each of which redraws a chart.
    /// </remarks>
    public DashboardMetric Metric
    {
        get => _metric;
        set
        {
            _metric = value;
            ValueText = Format(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    /// <summary>Who the chart is about — lets the explainer copy name them instead of a label.</summary>
    public string? MemberFirstName { get; }

    /// <summary>
    /// The legend entry for this member's own learned normal, or null for a metric that has none
    /// — SpO2 and breathing rate always, and any metric while the baseline is still learning.
    /// </summary>
    /// <remarks>
    /// The rule is named rather than labelled "Baseline": the word is the product's, not the
    /// caregiver's, and <see cref="MetricExplanations.BaselineLabel"/> is the one place that
    /// decides what to call it, so the key, the footer and the "i" panel never drift apart.
    /// Unitless, like the chart's own min/max labels: the unit is already on the headline reading
    /// a few dp above, and a legend is read as an annotation of the axis it sits under.
    /// </remarks>
    public string? BaselineText =>
        Metric.Baseline is { } baseline
            ? $"{MetricExplanations.BaselineLabel(Name)}: {string.Format(AxisFormat, baseline)}"
            : null;

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
    private string Format(DashboardMetric metric) =>
        metric.Value is { } value ? string.Format(_valueFormat, value) : "—";

    public IReadOnlyList<MetricPoint> Window =>
        Metric.Series.Count <= _days
            ? Metric.Series
            : Metric.Series.Skip(Metric.Series.Count - _days).ToList();
}
