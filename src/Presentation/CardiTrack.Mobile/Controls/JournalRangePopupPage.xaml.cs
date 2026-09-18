using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Export;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Which stretch of the CardiJournal to export, in the unit the reader is already looking at:
/// days on the Daybook, weeks on the Weekbook, months on the Monthbook. Two dates are picked in
/// the platform's calendar; the cadence decides how they are widened to whole periods, and the
/// summary reads the widened range back before the export runs.
/// </summary>
public partial class JournalRangePopupPage : ContentPage
{
    // RunContinuationsAsynchronously so completing this from CloseAsync/OnDisappearing (both
    // already on the UI thread) doesn't run the awaiter's continuation synchronously in-line.
    private readonly TaskCompletionSource<(DateOnly From, DateOnly To)?> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly JournalCadence _cadence;
    private readonly DateOnly _today;
    private bool _closing;

    public JournalRangePopupPage(JournalCadence cadence, DateOnly today)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent
        // modal renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        _cadence = cadence;
        _today = today;

        SubtitleLabel.Text = cadence switch
        {
            JournalCadence.Weekbook => "Whole weeks, Monday to Sunday",
            JournalCadence.Monthbook => "Whole months, first to last",
            _ => "One entry for each finished day",
        };

        // A year's ceiling on the API, so there is no reason to offer a date beyond it, and
        // nothing after today has been written yet.
        var latest = today.ToDateTime(TimeOnly.MinValue);
        var earliest = today.AddDays(-(JournalExportRequests.MaxRangeDays - 1))
            .ToDateTime(TimeOnly.MinValue);

        FromPicker.MinimumDate = earliest;
        FromPicker.MaximumDate = latest;
        ToPicker.MinimumDate = earliest;
        ToPicker.MaximumDate = latest;

        // Opens on the window the export used to take without asking: the last week of days,
        // the last month of weeks, the last quarter of months.
        var defaultSpan = cadence switch
        {
            JournalCadence.Weekbook => 28,
            JournalCadence.Monthbook => 90,
            _ => 7,
        };
        FromPicker.Date = today.AddDays(-(defaultSpan - 1)).ToDateTime(TimeOnly.MinValue);
        ToPicker.Date = latest;

        UpdateSummary();
    }

    /// <summary>The chosen range, or null when the popup was dismissed without exporting.</summary>
    public Task<(DateOnly From, DateOnly To)?> Closed => _closed.Task;

    /// <summary>Same width rule as the other popups; see <see cref="PopupCard"/>.</summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        PopupCard.Fit(Card, width);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = Scrim.FadeToAsync(1, 140);
        _ = Card.ScaleToAsync(1, 140, Easing.CubicOut);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_closing && !Navigation.ModalStack.Contains(this))
            _closed.TrySetResult(null);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    private void OnRangeChanged(object? sender, DateChangedEventArgs e) => UpdateSummary();

    private async void OnScrimTapped(object? sender, TappedEventArgs e) => await CloseAsync(null);

    private async void OnCloseTapped(object? sender, TappedEventArgs e) => await CloseAsync(null);

    private async void OnExportClicked(object? sender, EventArgs e) => await CloseAsync(Range());

    /// <summary>What the two fields currently mean, once the cadence has had its say.</summary>
    private (DateOnly From, DateOnly To) Range()
    {
        var from = DateOnly.FromDateTime(FromPicker.Date ?? _today.ToDateTime(TimeOnly.MinValue));
        var to = DateOnly.FromDateTime(ToPicker.Date ?? _today.ToDateTime(TimeOnly.MinValue));
        return JournalExportRequests.SnapToCadence(_cadence, from, to);
    }

    /// <summary>
    /// Reads the range back as it will actually be exported. The fields say what was picked;
    /// this says what that means once whole weeks or whole months have been taken in, which is
    /// the difference a caregiver would otherwise only discover in the file.
    /// </summary>
    private void UpdateSummary()
    {
        var (from, to) = Range();
        var count = JournalExportRequests.PeriodCount(_cadence, from, to);
        var noun = _cadence switch
        {
            JournalCadence.Weekbook => "week",
            JournalCadence.Monthbook => "month",
            _ => "day",
        };

        var period = count == 1 ? noun : $"{noun}s";
        SummaryLabel.Text = $"{count} {period} · {from:d MMM yyyy} to {to:d MMM yyyy}";
    }

    private async Task CloseAsync((DateOnly From, DateOnly To)? result)
    {
        if (_closing)
            return;
        _closing = true;

        try
        {
            await Task.WhenAll(
                Scrim.FadeToAsync(0, 100),
                Card.ScaleToAsync(0.92, 100, Easing.CubicIn));
            await Navigation.PopModalAsync(animated: false);
        }
        finally
        {
            _closed.TrySetResult(result);
        }
    }
}
