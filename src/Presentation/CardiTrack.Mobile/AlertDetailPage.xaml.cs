using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Charts;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// Alert detail (M1-11 / M1-12 / M1-16). One page, sections shown by the alert's rule — not
/// three nearly-identical pages. The chart is the series that caused the alert, never the
/// dashboard's six-metric payload.
/// </summary>
[QueryProperty(nameof(AlertId), "alertId")]
public partial class AlertDetailPage : ContentPage
{
    public const string Route = "alertdetail";

    private const int MarkerPointLimit = 14;
    private const double UnavailableActionOpacity = 0.4;

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private Guid _alertId;
    private bool _isLoading;
    private bool _returningFromPopup;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private AlertDetailResponse? _alert;

    public AlertDetailPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        this.RefreshWhenAppResumes(RefreshUnattendedAsync);
        this.RefreshEvery(PeriodicRefresh.LiveDataInterval, RefreshUnattendedAsync);
    }

    public string AlertId
    {
        set => _alertId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_popups.IsShowing || _returningFromPopup)
        {
            _returningFromPopup = false;
            return;
        }

        _ = LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _returningFromPopup = _popups.IsShowing;
    }

    private Task RefreshUnattendedAsync() =>
        DateTime.UtcNow - _lastLoadedUtc < ResumeRefresh.MinimumGap
            ? Task.CompletedTask
            : LoadAsync(silent: true);

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnBackClicked(object? sender, EventArgs e) =>
        await this.GoBackAsync(AppShell.AlertsRoute);

    private async Task LoadAsync(bool silent = false)
    {
        if (_isLoading || _alertId == Guid.Empty)
            return;
        _isLoading = true;

        if (_alert is null)
            SetState(loading: true);

        try
        {
            _alert = await _api.GetAlertAsync(_alertId);
            _lastLoadedUtc = DateTime.UtcNow;
            Apply(_alert);
            SetState(loaded: true);
        }
        catch (ApiException ex)
        {
            if (_alert is null)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(error: true);
            }
            else if (!silent)
            {
                await _popups.ShowWarningAsync(ex.Message, "Couldn't refresh");
            }
        }
        finally
        {
            _isLoading = false;
            Refresher.IsRefreshing = false;
        }
    }

    private void Apply(AlertDetailResponse alert)
    {
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var firstName = NameFormatting.FirstName(alert.CardiMemberName);

        var (badge, icon, bannerKey) = alert.Severity switch
        {
            "red" => ("CRITICAL", "icon_status_critical.svg", "StatusRed"),
            "orange" => ("URGENT", "icon_status_urgent.svg", "StatusOrange"),
            "yellow" => ("INFO", "icon_status_warning.svg", "StatusYellow"),
            _ => ("INFO", "icon_status_warning.svg", "StatusUnknown"),
        };

        SeverityBanner.BackgroundColor = (Color)resources[bannerKey];
        SeverityBadge.Text = badge;
        SeverityIcon.Source = icon;
        TitleLabel.Text = alert.Title;
        TimeLabel.Text = FormatTriggered(alert.TriggeredAt);

        Avatar.BoxWidth = 52;
        Avatar.Apply(alert.CardiMemberName, alert.CardiMemberPhotoUrl);
        MemberNameLabel.Text = string.IsNullOrWhiteSpace(alert.CardiMemberName)
            ? "CardiMember"
            : alert.CardiMemberName;
        MemberTypeLabel.Text = alert.Type;
        MessageLabel.Text = alert.Message;

        ApplyChart(alert.Chart);
        ApplyComparison(alert.Comparison);
        ApplyContext(alert, firstName);
        ApplyAcknowledgement(alert);
        ApplyActions(alert, firstName);
    }

    private void ApplyChart(AlertChartResponse? chart)
    {
        if (chart is null || chart.Series.Count < 2)
        {
            ChartCard.IsVisible = false;
            return;
        }

        ChartCard.IsVisible = true;
        ChartNameLabel.Text = chart.Name;
        ChartWindowLabel.Text = chart.WindowLabel;
        ChartValueLabel.Text = chart.Value is { } value
            ? string.Format(ValueFormat(chart.Metric), value)
            : "—";
        ChartIcon.Source = chart.Metric switch
        {
            "steps" => "icon_metric_steps.svg",
            "sleep" => "icon_metric_sleep.svg",
            _ => "icon_metric_heart.svg",
        };

        var inkKey = chart.Metric switch
        {
            "steps" => "MetricStepsInk",
            "sleep" => "MetricSleepInk",
            _ => "MetricHeartInk",
        };
        var ink = MetricStatus.Resource(inkKey, Colors.Gray);

        var values = chart.Series.Where(p => p.Value is not null).Select(p => (double)p.Value!).ToList();
        if (values.Count == 0)
        {
            ChartCard.IsVisible = false;
            return;
        }

        var baseline = chart.Baseline is { } b ? (double)b : (double?)null;
        var scale = TrendScale.For(values.Min(), values.Max(), baseline, null, null);
        Chart.Render(
            chart.Series,
            scale,
            ink,
            showMarkers: chart.Series.Count <= MarkerPointLimit,
            baseline: chart.Baseline);

        ChartBaselineLabel.IsVisible = chart.Baseline is not null;
        ChartBaselineLabel.Text = chart.Baseline is { } usual
            ? $"Dashed: their usual {string.Format(AxisFormat(chart.Metric), usual)}"
            : string.Empty;
    }

    private void ApplyComparison(AlertComparisonResponse? comparison)
    {
        if (comparison is null)
        {
            ComparisonCard.IsVisible = false;
            return;
        }

        ComparisonCard.IsVisible = true;
        CurrentLabel.Text = comparison.CurrentLabel;
        CurrentValueLabel.Text = comparison.CurrentValue;
        NormalLabel.Text = comparison.NormalLabel;
        NormalValueLabel.Text = comparison.NormalValue;
        ChangeLabel.IsVisible = !string.IsNullOrWhiteSpace(comparison.ChangeLabel);
        ChangeLabel.Text = comparison.ChangeLabel ?? string.Empty;
    }

    private void ApplyContext(AlertDetailResponse alert, string firstName)
    {
        var who = string.IsNullOrWhiteSpace(firstName) ? "they" : firstName;
        string? copy = alert.Rule switch
        {
            "no_morning_activity" when alert.LastActivityOn is { } last =>
                $"{who} usually wakes around {alert.TypicalWakeTime ?? "this time"}. Last movement we saw was {last:d MMM}.",
            "device_silence" when alert.LastDataAt is { } at =>
                $"The device last sent a reading {RelativeTime.Format(at)}. It may need charging, or a check that it is being worn.",
            _ => null,
        };

        ContextCard.IsVisible = copy is not null;
        ContextLabel.Text = copy ?? string.Empty;
    }

    private void ApplyAcknowledgement(AlertDetailResponse alert)
    {
        var handled = alert.Status is "acknowledged" or "resolved";
        AcknowledgeButton.IsVisible = !handled;
        AcknowledgeButton.Text = alert.Severity == "red" ? "I'm on my way" : "Mark as acknowledged";

        if (!handled)
        {
            AcknowledgedLabel.IsVisible = false;
            return;
        }

        var who = string.IsNullOrWhiteSpace(alert.AcknowledgedByName)
            ? "Acknowledged"
            : $"Acknowledged by {NameFormatting.FirstName(alert.AcknowledgedByName)}";
        var when = alert.AcknowledgedAt is { } at ? RelativeTime.Format(at) : null;
        AcknowledgedLabel.Text = when is null ? who : $"{who}, {when}";
        AcknowledgedLabel.IsVisible = true;
    }

    private void ApplyActions(AlertDetailResponse alert, string firstName)
    {
        var isCritical = alert.Severity == "red";
        var emergency = !string.IsNullOrWhiteSpace(alert.EmergencyContactPhone);
        var memberPhone = !string.IsNullOrWhiteSpace(alert.Phone);

        CallButton.Text = isCritical
            ? "Call now"
            : string.IsNullOrWhiteSpace(firstName) ? "Call" : $"Call {firstName}";

        // Critical alerts dial the emergency contact (M1-12); others match M1-10 and dial
        // the emergency number too when that is all we have, else the member.
        CallButton.Opacity = (isCritical ? emergency : memberPhone || emergency) ? 1 : UnavailableActionOpacity;

        MessageButton.Text = string.IsNullOrWhiteSpace(firstName) ? "Send a message" : $"Message {firstName}";
        MessageButton.IsVisible = !isCritical;
        MessageButton.Opacity = memberPhone ? 1 : UnavailableActionOpacity;
    }

    private static string FormatTriggered(DateTime utc)
    {
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return local.ToString("d MMMM yyyy 'at' h:mm tt");
    }

    private static string ValueFormat(string metric) => metric switch
    {
        "steps" => "{0:N0} steps",
        "sleep" => "{0:0.#} hours",
        _ => "{0:N0} bpm",
    };

    private static string AxisFormat(string metric) => metric switch
    {
        "sleep" => "{0:0.#}",
        _ => "{0:N0}",
    };

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
    }

    private async void OnCallClicked(object? sender, EventArgs e)
    {
        if (_alert is null)
            return;

        var isCritical = _alert.Severity == "red";
        var number = isCritical
            ? _alert.EmergencyContactPhone
            : FirstNonEmpty(_alert.Phone, _alert.EmergencyContactPhone);

        if (string.IsNullOrWhiteSpace(number))
        {
            await OfferToAddNumberAsync(isCritical);
            return;
        }

        try
        {
            PhoneDialer.Default.Open(number);
        }
        catch (Exception)
        {
            await _popups.ShowWarningAsync("Phone calls aren't supported on this device.");
        }
    }

    private async void OnMessageClicked(object? sender, EventArgs e)
    {
        if (_alert is null)
            return;

        if (string.IsNullOrWhiteSpace(_alert.Phone))
        {
            await OfferToAddNumberAsync(emergency: false);
            return;
        }

        try
        {
            await Sms.Default.ComposeAsync(new SmsMessage(string.Empty, _alert.Phone));
        }
        catch (Exception)
        {
            await _popups.ShowWarningAsync("Messaging isn't supported on this device.");
        }
    }

    private async void OnAcknowledgeClicked(object? sender, EventArgs e)
    {
        if (_alert is null)
            return;

        AcknowledgeButton.IsEnabled = false;
        try
        {
            var result = await _api.AcknowledgeAlertAsync(_alert.AlertId);
            _alert.Status = result.Status;
            _alert.AcknowledgedAt = result.AcknowledgedAt;
            _alert.AcknowledgedByUserId = result.AcknowledgedByUserId;
            ApplyAcknowledgement(_alert);
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't mark it handled");
        }
        finally
        {
            AcknowledgeButton.IsEnabled = true;
        }
    }

    private async Task OfferToAddNumberAsync(bool emergency)
    {
        var firstName = NameFormatting.FirstName(_alert?.CardiMemberName);
        var prompt = emergency
            ? string.IsNullOrWhiteSpace(firstName)
                ? "Would you like to add an emergency contact number, so you can call them from here?"
                : $"Would you like to add an emergency contact number for {firstName}, so you can call them from here?"
            : string.IsNullOrWhiteSpace(firstName)
                ? "Would you like to add a phone number, so you can call or message them from here?"
                : $"Would you like to add a phone number for {firstName}, so you can call or message them from here?";

        var addNow = await _popups.ConfirmInfoAsync(prompt, "No number yet", "Add number", "Not now");
        if (addNow && _alert is { } alert)
            await Shell.Current.GoToAsync($"{EditCardiMemberPage.Route}?memberId={alert.CardiMemberId}");
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
