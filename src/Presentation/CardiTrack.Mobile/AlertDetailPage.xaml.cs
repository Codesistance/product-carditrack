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

        // NOTICE, not INFO, for yellow. The badge word and the banner fill are both read off
        // severity, so a yellow alert used to put the mildest word in the vocabulary on an amber
        // banner and read as a contradiction — and "INFO" was doing duty for green as well, which
        // flattened "nothing to report" and "something is different" into one word. The colour is
        // deliberately unchanged: see AlertListCard, which colours by our own severity scale
        // rather than by Figma's blue INFO chip so a badge can never disagree with the rail
        // beside it. This diverges from M1-10's CRITICAL/URGENT/INFO wording on purpose.
        var (badge, bannerKey) = alert.Severity switch
        {
            "red" => ("CRITICAL", "StatusRed"),
            "orange" => ("URGENT", "StatusOrange"),
            "yellow" => ("NOTICE", "StatusYellow"),
            _ => ("INFO", "StatusUnknown"),
        };

        SeverityBanner.BackgroundColor = (Color)resources[bannerKey];
        SeverityBadge.Text = badge;
        ReasonIcon.Source = ReasonIconFor(alert.Reason);
        TitleLabel.Text = alert.Title;
        TimeLabel.Text = FormatWhen(alert);

        Avatar.BoxWidth = 52;
        Avatar.Apply(alert.CardiMemberName, alert.CardiMemberPhotoUrl);
        MemberNameLabel.Text = string.IsNullOrWhiteSpace(alert.CardiMemberName)
            ? "CardiMember"
            : alert.CardiMemberName;
        MemberTypeLabel.Text = alert.Type;
        MessageLabel.Text = alert.Message;

        ApplyChart(alert.Chart);
        ApplyComparison(alert.Comparison, alert.Severity);
        ApplyContext(alert, firstName);
        ApplyAcknowledgement(alert);

        QuickActions.Apply(
            new QuickActionTarget(
                alert.CardiMemberId,
                alert.CardiMemberName,
                alert.Phone,
                alert.EmergencyContactPhone,
                alert.EmergencyContactName),
            _popups);
    }

    /// <summary>
    /// The reason icon, in the white-stroke variants the coloured banner needs. An unrecognised
    /// key falls back to the catch-all rather than to no icon: a server that learns a new reason
    /// before the app does should leave the banner looking ordinary, not empty.
    /// </summary>
    private static string ReasonIconFor(string? reason) => reason switch
    {
        AlertReasons.Activity => "icon_reason_activity_white.svg",
        AlertReasons.Heart => "icon_reason_heart_white.svg",
        AlertReasons.Sleep => "icon_reason_sleep_white.svg",
        AlertReasons.Device => "icon_reason_device_white.svg",
        _ => "icon_reason_monitoring_white.svg",
    };

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
        ChartValueDayLabel.Text = chart.ValueLabel ?? string.Empty;
        ChartValueDayLabel.IsVisible = !string.IsNullOrWhiteSpace(chart.ValueLabel);
        ChartPartialDayLabel.Text = chart.PartialDayLabel ?? string.Empty;
        ChartPartialDayLabel.IsVisible = !string.IsNullOrWhiteSpace(chart.PartialDayLabel);
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

    private void ApplyComparison(AlertComparisonResponse? comparison, string severity)
    {
        if (comparison is null)
        {
            ComparisonCard.IsVisible = false;
            return;
        }

        ComparisonCard.IsVisible = true;
        CurrentLabel.Text = comparison.CurrentLabel;
        NormalLabel.Text = comparison.NormalLabel;
        SetValue(CurrentValueNumber, CurrentValueUnit, comparison.CurrentValue);
        SetValue(NormalValueNumber, NormalValueUnit, comparison.NormalValue);

        var hasChange = !string.IsNullOrWhiteSpace(comparison.ChangeLabel);
        ChangeBand.IsVisible = hasChange;
        if (!hasChange)
            return;

        // The arrow says which way, the tint says how much it matters. The tint is the alert's own
        // severity rather than a fixed red: this comparison is the reason the alert exists, so a
        // band louder than the alert would have the same screen disagreeing with itself — the
        // failure the badge word beside it was just corrected for.
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var (tintKey, inkKey) = severity switch
        {
            "red" => ("PillRedBackground", "StatusRed"),
            "orange" => ("PillOrangeBackground", "StatusOrange"),
            "yellow" => ("PillYellowBackground", "StatusYellow"),
            _ => ("PillNeutralBackground", "StatusUnknown"),
        };

        ChangeBand.BackgroundColor = (Color)resources[tintKey];
        ChangeLabel.TextColor = (Color)resources[inkKey];

        // No arrow when the reading is in line with normal — there is no direction to point.
        var arrow = comparison.ChangePercent switch
        {
            < 0 => "↓ ",
            > 0 => "↑ ",
            _ => string.Empty,
        };
        ChangeLabel.Text = $"{arrow}{comparison.ChangeLabel}";
    }

    /// <summary>
    /// Splits a formatted reading into the figure and its unit, so the two can be set at different
    /// weights. Every producer in <c>AlertDetailComposer</c> formats these as "{number} {unit}"
    /// ("1,477 steps", "68 bpm", "7.2 hours"), and the ones that don't carry a unit at all — an
    /// em dash for a missing reading, a wake time — have no space to split on and pass through
    /// whole.
    /// </summary>
    private static void SetValue(Span number, Span unit, string value)
    {
        var split = value.IndexOf(' ');
        number.Text = split < 0 ? value : value[..split];
        unit.Text = split < 0 ? string.Empty : value[split..];
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

    /// <summary>
    /// The handled state and its undo. Acknowledged offers Undo; resolved does not — resolution is
    /// the system's own judgement that the condition has passed, and the endpoint refuses to
    /// reopen it, so offering a button that would come back with an error would be a worse answer
    /// than not offering one.
    /// </summary>
    private void ApplyAcknowledgement(AlertDetailResponse alert)
    {
        var acknowledged = alert.Status == "acknowledged";
        var handled = acknowledged || alert.Status == "resolved";

        AcknowledgeButton.IsVisible = !handled;
        AcknowledgeButton.Text = alert.Severity == "red" ? "I'm on my way" : "Mark as acknowledged";
        UndoAcknowledgeButton.IsVisible = acknowledged;

        if (!handled)
        {
            AcknowledgedLabel.IsVisible = false;
            return;
        }

        AcknowledgedLabel.Text = HandledLabel(alert);
        AcknowledgedLabel.IsVisible = true;
    }

    /// <summary>
    /// What closed this alert. The two states are not the same claim and must not share a
    /// sentence: <c>AlertResolution.Resolve</c> sets <c>IsResolved</c> from the producer's own
    /// "the condition has passed" test and never looks at who acknowledged, so a resolved alert
    /// nobody touched has no acknowledger and no acknowledgement time. Keying the copy off the
    /// handled state rather than off <see cref="AlertDetailResponse.AcknowledgedAt"/> told the
    /// caregiver a bare "Acknowledged" about an episode that had simply settled by itself.
    /// </summary>
    private static string HandledLabel(AlertDetailResponse alert)
    {
        if (alert.AcknowledgedAt is not { } at)
            return "This settled on its own — no action needed";

        var who = string.IsNullOrWhiteSpace(alert.AcknowledgedByName)
            ? "Acknowledged"
            : $"Acknowledged by {NameFormatting.FirstName(alert.AcknowledgedByName)}";

        return $"{who}, {RelativeTime.Format(at)}";
    }

    /// <summary>
    /// Daily-grain alerts are about a civil day, not a clock time — showing the afternoon we
    /// noticed a quieter yesterday dated the quieter day as today. When <see cref="AlertDetailResponse.AboutDate"/>
    /// is a different calendar day from the raise, print that day and drop the clock.
    /// </summary>
    private static string FormatWhen(AlertDetailResponse alert)
    {
        var local = DateTime.SpecifyKind(alert.TriggeredAt, DateTimeKind.Utc).ToLocalTime();
        if (alert.AboutDate != default && alert.AboutDate != DateOnly.FromDateTime(local))
            return alert.AboutDate.ToString("d MMMM yyyy");

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

    private void OnAcknowledgeClicked(object? sender, EventArgs e) =>
        _ = SetAcknowledgedAsync(handled: true);

    private void OnUndoAcknowledgeClicked(object? sender, EventArgs e) =>
        _ = SetAcknowledgedAsync(handled: false);

    /// <summary>
    /// Both directions of the same toggle. The response is applied to the alert already on screen
    /// rather than triggering a reload — the caregiver is looking at this card, and a full refetch
    /// would blink the whole page for one field.
    /// </summary>
    private async Task SetAcknowledgedAsync(bool handled)
    {
        if (_alert is not { } alert)
            return;

        AcknowledgeButton.IsEnabled = false;
        UndoAcknowledgeButton.IsEnabled = false;
        try
        {
            var result = handled
                ? await _api.AcknowledgeAlertAsync(alert.AlertId)
                : await _api.UnacknowledgeAlertAsync(alert.AlertId);

            alert.Status = result.Status;
            alert.AcknowledgedAt = result.AcknowledgedAt;
            alert.AcknowledgedByUserId = result.AcknowledgedByUserId;

            // The name belongs to whoever acknowledged it, so it has to go when the acknowledgement
            // does — otherwise undoing leaves a stale "Acknowledged by Sam" behind the next tap.
            if (!handled)
                alert.AcknowledgedByName = null;

            ApplyAcknowledgement(alert);
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(
                ex.Message, handled ? "Couldn't mark it handled" : "Couldn't undo that");
        }
        finally
        {
            AcknowledgeButton.IsEnabled = true;
            UndoAcknowledgeButton.IsEnabled = true;
        }
    }

    private async void OnViewActivityDataTapped(object? sender, TappedEventArgs e)
    {
        if (_alert is { } alert)
            await Shell.Current.GoToAsync($"{CardiMemberDetailPage.Route}?memberId={alert.CardiMemberId}");
    }

    /// <summary>
    /// Hands the alert to whatever the caregiver already uses to talk to their family. Deliberately
    /// the same three facts the sharer can see on the banner in front of them — who, what, and
    /// when — and none of the numbers below it: this leaves the app for an arbitrary destination,
    /// so it carries the least that still makes the message worth sending.
    /// </summary>
    private async void OnShareWithFamilyTapped(object? sender, TappedEventArgs e)
    {
        if (_alert is not { } alert)
            return;

        var firstName = NameFormatting.FirstName(alert.CardiMemberName);
        var who = string.IsNullOrWhiteSpace(firstName) ? "a CardiMember" : firstName;

        try
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = "Share this alert",
                Text = $"CardiTrack alert for {who}: {alert.Title} ({FormatWhen(alert)}).",
            });
        }
        catch (Exception)
        {
            await _popups.ShowWarningAsync("Sharing isn't supported on this device.");
        }
    }
}
