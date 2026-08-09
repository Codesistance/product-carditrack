using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>M1-10 Alerts List — every alert across the CardiMembers this caregiver watches.</summary>
public partial class AlertsPage : ContentPage
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gap above the empty card, matching Figma. Two values because the card sits at the same
    /// y in both frames while the chip row above it is only present in one.
    /// </summary>
    private const double EmptyTopWithoutChips = 182;
    private const double EmptyTopWithChips = 124;

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private enum AlertsState { Loading, Loaded, Error }

    private bool _isLoading;
    private bool _showArchived;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private AlertListResponse? _lastData;

    public AlertsPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        Filters.FilterChanged += OnFilterChanged;
        ApplyArchiveButtonText();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_lastData is null || DateTime.UtcNow - _lastLoadedUtc > AutoRefreshInterval)
            _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_isLoading)
            return;
        _isLoading = true;

        if (_lastData is null)
            SetState(AlertsState.Loading);

        try
        {
            var (severity, status, from) = CurrentQuery();
            var data = await _api.GetAlertsAsync(severity, status, from);
            _lastData = data;
            _lastLoadedUtc = DateTime.UtcNow;
            Render(data);
            SetState(AlertsState.Loaded);
        }
        catch (ApiException ex)
        {
            if (_lastData is null)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(AlertsState.Error);
            }
            else
            {
                // Alerts already on screen: a failed refresh must not blank a list someone
                // may be acting on, so say so and leave it.
                await _popups.ShowWarningAsync(ex.Message, "Couldn't refresh");
            }
        }
        finally
        {
            _isLoading = false;
            Refresher.IsRefreshing = false;
        }
    }

    /// <summary>
    /// The chip selection as wire filters. Archived overrides the chips entirely — it is a
    /// different list, not a narrower one.
    /// </summary>
    private (string? Severity, string? Status, DateTime? From) CurrentQuery()
    {
        if (_showArchived)
            return (null, "resolved", null);

        // Local midnight, not UTC: "Today" has to mean the caregiver's today.
        var todayStart = DateTime.Today;

        return Filters.Selected switch
        {
            AlertFilter.Unread => (null, "new", null),
            AlertFilter.Critical => ("red", null, null),
            AlertFilter.Today => (null, null, todayStart),
            AlertFilter.ThisWeek => (null, null, todayStart.AddDays(-6)),
            _ => (null, null, null),
        };
    }

    private void Render(AlertListResponse data)
    {
        GroupsStack.Clear();

        var hasAlerts = data.Alerts.Count > 0;
        GroupsStack.IsVisible = hasAlerts;
        EmptyPanel.IsVisible = !hasAlerts;

        // A dead end otherwise: with no alerts and no way back, an empty archive would trap
        // the caregiver on a blank screen.
        ArchiveButton.IsVisible = hasAlerts || _showArchived;

        // Nothing to filter when the unfiltered list is genuinely empty — Figma's M1-10b drops
        // the chip row entirely, and an archive listing isn't chip-filtered at all.
        var isUnfiltered = Filters.Selected == AlertFilter.All && !_showArchived;
        Filters.IsVisible = !_showArchived && !(isUnfiltered && !hasAlerts);

        if (!hasAlerts)
        {
            var (title, detail) = isUnfiltered
                ? ("Nothing to worry about",
                   "CardiTrack is keeping an eye on things — we'll let you know if anything comes up")
                : ("No alerts match this filter",
                   "Try selecting a different filter to see more alerts");

            EmptyTitleLabel.Text = title;
            EmptyDetailLabel.Text = detail;
            EmptyPanel.Margin = new Thickness(
                0, Filters.IsVisible ? EmptyTopWithChips : EmptyTopWithoutChips, 0, 0);
            return;
        }

        EmptyPanel.Margin = new Thickness(0);

        var sectionTitle = (Style)Microsoft.Maui.Controls.Application.Current!.Resources["Heading3"];

        foreach (var group in GroupByDate(data.Alerts))
        {
            var section = new VerticalStackLayout { Spacing = 13 };
            section.Add(new Label { Text = group.Key, Style = sectionTitle });

            foreach (var alert in group)
            {
                var card = new AlertListCard();
                card.Apply(alert);
                card.CallRequested += OnCallRequested;
                card.AcknowledgeRequested += OnAcknowledgeRequested;
                section.Add(card);
            }

            GroupsStack.Add(section);
        }
    }

    /// <summary>
    /// Date buckets in the order M1-10 specifies: Today, Yesterday, This Week, Older.
    /// Timestamps arrive in UTC and are bucketed in local time, so an alert raised at 23:30
    /// local doesn't land under "Yesterday".
    /// </summary>
    private static IEnumerable<IGrouping<string, AlertSummaryResponse>> GroupByDate(
        IEnumerable<AlertSummaryResponse> alerts)
    {
        var today = DateTime.Today;

        return alerts
            .GroupBy(a =>
            {
                var local = DateTime.SpecifyKind(a.TriggeredAt, DateTimeKind.Utc).ToLocalTime().Date;
                if (local == today) return "Today";
                if (local == today.AddDays(-1)) return "Yesterday";
                return local > today.AddDays(-7) ? "This Week" : "Older";
            })
            .OrderBy(g => g.Key switch
            {
                "Today" => 0,
                "Yesterday" => 1,
                "This Week" => 2,
                _ => 3,
            });
    }

    private void SetState(AlertsState state)
    {
        SkeletonPanel.IsVisible = state == AlertsState.Loading;
        ErrorPanel.IsVisible = state == AlertsState.Error;
        ContentPanel.IsVisible = state == AlertsState.Loaded;

        // The chip row belongs to the list, not to the error or the first load.
        if (state != AlertsState.Loaded)
            Filters.IsVisible = state == AlertsState.Loading && !_showArchived;
    }

    private void OnFilterChanged(object? sender, AlertFilter filter)
    {
        // A filter change is a different query, so the cached page no longer applies —
        // dropping it puts the skeleton back rather than leaving stale rows under new chips.
        _lastData = null;
        _ = LoadAsync();
    }

    private async void OnArchiveClicked(object? sender, EventArgs e)
    {
        _showArchived = !_showArchived;
        _lastData = null;
        ApplyArchiveButtonText();
        if (!_showArchived)
            Filters.SetSelectedSilently(AlertFilter.All);
        await LoadAsync();
    }

    private void ApplyArchiveButtonText() =>
        ArchiveButton.Text = _showArchived ? "Back to current alerts" : "View Archived Alerts";

    /// <summary>
    /// Alerts is a tab root, so there is no stack to pop — the arrow goes where it looks like
    /// it goes, back to the dashboard, rather than unwinding to wherever the user came from.
    /// </summary>
    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(AppShell.DashboardRoute);

    /// <summary>
    /// The header's filter button. It offers the same five filters as the chips, because the
    /// empty state (101:3840) keeps this button and drops the chip row.
    /// </summary>
    private async void OnFilterTapped(object? sender, TappedEventArgs e)
    {
        var labels = FilterChipBar.Options.Select(o => o.Label).ToArray();
        var chosen = await _popups.ChooseAsync("Filter alerts", "Cancel", labels);
        if (chosen is null)
            return;

        var filter = FilterChipBar.Options.First(o => o.Label == chosen).Filter;

        // Picking a filter means "show me current alerts like this", so it leaves the archive.
        if (_showArchived)
        {
            _showArchived = false;
            ApplyArchiveButtonText();
            Filters.SetSelectedSilently(filter);
            _lastData = null;
            await LoadAsync();
            return;
        }

        Filters.Select(filter);
    }

    private void OnPullToRefresh(object? sender, EventArgs e) => _ = LoadAsync();

    private void OnRefreshClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnCallRequested(object? sender, AlertSummaryResponse alert)
    {
        if (string.IsNullOrWhiteSpace(alert.EmergencyContactPhone))
        {
            await _popups.ShowInfoAsync(
                $"There's no phone number saved for {NameFormatting.FirstName(alert.CardiMemberName)} yet. "
                + "Add an emergency contact on their profile and you'll be able to call from here.",
                "No number yet");
            return;
        }

        try
        {
            PhoneDialer.Default.Open(alert.EmergencyContactPhone);
        }
        catch (FeatureNotSupportedException)
        {
            await _popups.ShowWarningAsync("Phone calls aren't supported on this device.");
        }
    }

    private async void OnAcknowledgeRequested(object? sender, AlertSummaryResponse alert)
    {
        if (sender is not AlertListCard card)
            return;

        card.SetBusy(true);
        try
        {
            var result = await _api.AcknowledgeAlertAsync(alert.AlertId);
            alert.Status = result.Status;
            alert.AcknowledgedAt = result.AcknowledgedAt;
            alert.AcknowledgedByUserId = result.AcknowledgedByUserId;
            if (_lastData is not null)
                _lastData.UnreadCount = result.UnreadCount;

            // Re-applied rather than reloaded: the caregiver is looking at this row, and a full
            // reload would reshuffle the list under their thumb.
            card.Apply(alert);
        }
        catch (ApiException ex)
        {
            card.SetBusy(false);
            await _popups.ShowWarningAsync(ex.Message, "Couldn't mark it handled");
        }
    }
}
