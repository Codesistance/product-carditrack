using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Alarms;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// Building or editing one alarm.
/// </summary>
/// <remarks>
/// <para>
/// Every choice narrows the ones below it, driven by the server's catalogue through
/// <see cref="AlarmDraft"/> — the rules and the narrowing live there so they can be tested without
/// a MAUI host, and this page is the pickers plus the wiring. The promise is that an alarm which
/// cannot work is unreachable rather than refused at Save: switching to a daily reading drops the
/// short windows, switching to a level reading drops "total", and a reading CardiTrack has learned
/// no usual for drops the compare-to-usual options.
/// </para>
/// <para>
/// Unlike the list next door, this page holds its changes until Save. A half-built alarm is not a
/// state worth persisting, and a threshold typed one digit at a time would otherwise be saved at
/// every keystroke.
/// </para>
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(MemberName), "name")]
[QueryProperty(nameof(AlarmId), "alarmId")]
public partial class MetricAlarmEditPage : ContentPage
{
    public const string Route = "metricalarmedit";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private Guid _memberId;
    private Guid? _alarmId;
    private AlarmProvenance? _provenance;
    private AlarmDraft? _draft;
    private bool _loaded;

    /// <summary>Guards every handler while we rebuild the pickers from the draft.</summary>
    private bool _applying;

    private bool _saving;

    public MetricAlarmEditPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    public string MemberName
    {
        set
        {
            var name = Uri.UnescapeDataString(value ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(name))
                HeaderSubtitle.Text = $"For {name}";
        }
    }

    public string AlarmId
    {
        set => _alarmId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : null;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_popups.IsShowing || _loaded)
            return;

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var catalogue = await _api.GetAlarmCatalogueAsync();

            MetricAlarmResponse? existing = null;
            if (_alarmId is { } id)
            {
                var alarms = await _api.GetMemberAlarmsAsync(_memberId);
                existing = alarms.FirstOrDefault(a => a.Id == id);
            }

            _draft = new AlarmDraft(catalogue, existing);
            _loaded = true;

            _provenance = existing?.Provenance;

            HeaderTitle.Text = existing is null ? "New Alarm" : "Edit Alarm";

            // An inherited alarm is the account's, not this member's, so there is nothing here to
            // delete — switching it off on the list writes the opt-out, which is what "not for this
            // person" means. Offering Remove would send a caregiver to a 404 for asking a
            // reasonable question. An override, on the other hand, can be removed: that puts the
            // account's own setting back.
            DeleteButton.IsVisible = existing is not null && _provenance != AlarmProvenance.Inherited;
            DeleteButton.Text = _provenance == AlarmProvenance.Overridden
                ? "Use the account setting instead"
                : "Remove this alarm";

            LoadingSpinner.IsVisible = false;
            LoadingSpinner.IsRunning = false;
            FormPanel.IsVisible = true;
            SaveButton.IsVisible = true;

            BuildPickers();
            Refresh();
        }
        catch (ApiException ex)
        {
            ErrorDetailLabel.Text = ex.Message;
            LoadingSpinner.IsVisible = false;
            LoadingSpinner.IsRunning = false;
            ErrorPanel.IsVisible = true;
        }
    }

    /// <summary>The pickers whose contents never change — everything else is rebuilt by <see cref="Refresh"/>.</summary>
    private void BuildPickers()
    {
        _applying = true;
        try
        {
            MetricPicker.ItemsSource = _draft!.Metrics.Select(m => m.Title).ToList();

            OperatorPicker.ItemsSource = new List<string>
            {
                "goes above", "reaches", "drops below", "drops to",
            };

            SeverityPicker.ItemsSource = new List<string>
            {
                "Good to know — shows in the app",
                "Worth a look — sends a notification",
                "Urgent — wakes you through quiet hours",
            };

            MissingDataPicker.ItemsSource = new List<string>
            {
                "Say nothing until readings come back",
                "Treat the gap as normal",
                "Keep whatever the alarm was saying",
            };

            NameEntry.Text = _draft.Request.Name;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// Rebuilds every dependent picker from the draft. Called after any change, because a change
    /// anywhere can re-pick something further down — that is the whole point of the draft.
    /// </summary>
    private void Refresh()
    {
        if (_draft is null)
            return;

        _applying = true;
        try
        {
            var request = _draft.Request;

            MetricPicker.SelectedIndex = IndexOf(_draft.Metrics.Select(m => m.Metric).ToList(), request.Metric);

            var statistics = _draft.Statistics;
            StatisticPicker.ItemsSource = statistics.Select(StatisticLabel).ToList();
            StatisticPicker.SelectedIndex = IndexOf(statistics, request.Statistic);
            // A single-choice picker is a control that cannot be used. Daily readings are one value
            // per day, so "taken as" has nothing to ask.
            StatisticRow.IsVisible = statistics.Count > 1;

            OperatorPicker.SelectedIndex = request.Operator switch
            {
                AlarmOperator.GreaterThan => 0,
                AlarmOperator.GreaterThanOrEqualTo => 1,
                AlarmOperator.LessThan => 2,
                _ => 3,
            };

            var kinds = _draft.ThresholdKinds;
            ThresholdKindPicker.ItemsSource = kinds.Select(ThresholdKindLabel).ToList();
            ThresholdKindPicker.SelectedIndex = IndexOf(kinds, request.ThresholdKind);
            ThresholdKindRow.IsVisible = kinds.Count > 1;

            var (min, max) = _draft.ThresholdRange;
            ThresholdLabel.Text = request.ThresholdKind switch
            {
                AlarmThresholdKind.BaselinePercent => "Share of their usual",
                AlarmThresholdKind.BaselineSigma => "How far from their usual",
                _ => "Level",
            };
            ThresholdHint.Text = request.ThresholdKind switch
            {
                AlarmThresholdKind.BaselinePercent => $"Between {Format(min)}% and {Format(max)}% of their usual",
                AlarmThresholdKind.BaselineSigma => $"Between {Format(min)}× and {Format(max)}× their usual variation",
                _ => $"Between {Format(min)} and {Format(max)} {_draft.ThresholdUnit}".TrimEnd(),
            };
            ThresholdEntry.Text = Format(request.ThresholdValue);
            // The field now shows a number again, whatever was half-typed before this refresh, and
            // the draft's own record of the field has to agree with it.
            _draft.SetThresholdText(ThresholdEntry.Text);

            var periods = _draft.Periods;
            PeriodPicker.ItemsSource = periods.Select(PeriodLabel).ToList();
            PeriodPicker.SelectedIndex = IndexOf(periods, request.PeriodMinutes);
            PeriodRow.IsVisible = periods.Count > 1;

            EvaluationStepper.Maximum = _draft.MaxEvaluationPeriods;
            EvaluationStepper.Value = request.EvaluationPeriods;
            EvaluationLabel.Text = $"Look at the last {request.EvaluationPeriods} {Readings(request.EvaluationPeriods)}";

            DatapointsStepper.Maximum = request.EvaluationPeriods;
            DatapointsStepper.Value = request.DatapointsToAlarm;
            DatapointsLabel.Text =
                $"Tell me when {request.DatapointsToAlarm} of them {(request.DatapointsToAlarm == 1 ? "crosses" : "cross")} the line";

            GateRow.IsVisible = _draft.SupportsContextGate;
            GateSwitch.IsToggled = request.ContextGate == AlarmContextGate.Inactive;

            SeverityPicker.SelectedIndex = request.Severity switch
            {
                AlertSeverity.Red => 2,
                AlertSeverity.Orange => 1,
                _ => 0,
            };
            SeverityHint.Text = request.Severity == AlertSeverity.Red
                ? "Red alarms push through quiet hours and go on to other carers if nobody acknowledges them."
                : string.Empty;

            MissingDataPicker.SelectedIndex = request.MissingDataTreatment switch
            {
                AlarmMissingDataTreatment.NotBreaching => 1,
                AlarmMissingDataTreatment.Ignore => 2,
                _ => 0,
            };

            PreviewLabel.Text = _draft.Describe();

            // Errors are shown only for the fields a caregiver has actually reached — an empty
            // name on a form they have just opened is not a mistake yet.
            var errors = _draft.Validate()
                .Where(e => e.Field != nameof(request.Name) || !string.IsNullOrEmpty(NameEntry.Text))
                .ToList();
            ValidationLabel.Text = errors.Count > 0 ? errors[0].Message : string.Empty;
            ValidationLabel.IsVisible = errors.Count > 0;
        }
        finally
        {
            _applying = false;
        }
    }

    // ── handlers ─────────────────────────────────────────────────────────────────────────

    private void OnNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (_applying || _draft is null)
            return;

        _draft.Request.Name = e.NewTextValue ?? string.Empty;
        Refresh();
    }

    private void OnMetricChanged(object? sender, EventArgs e)
    {
        if (_applying || _draft is null || MetricPicker.SelectedIndex < 0)
            return;

        _draft.SelectMetric(_draft.Metrics[MetricPicker.SelectedIndex].Metric);
        Refresh();
    }

    private void OnStatisticChanged(object? sender, EventArgs e)
    {
        if (_applying || _draft is null || StatisticPicker.SelectedIndex < 0)
            return;

        _draft.Request.Statistic = _draft.Statistics[StatisticPicker.SelectedIndex];
        Refresh();
    }

    private void OnOperatorChanged(object? sender, EventArgs e)
    {
        if (_applying || _draft is null || OperatorPicker.SelectedIndex < 0)
            return;

        _draft.Request.Operator = OperatorPicker.SelectedIndex switch
        {
            0 => AlarmOperator.GreaterThan,
            1 => AlarmOperator.GreaterThanOrEqualTo,
            2 => AlarmOperator.LessThan,
            _ => AlarmOperator.LessThanOrEqualTo,
        };
        Refresh();
    }

    private void OnThresholdKindChanged(object? sender, EventArgs e)
    {
        if (_applying || _draft is null || ThresholdKindPicker.SelectedIndex < 0)
            return;

        _draft.SelectThresholdKind(_draft.ThresholdKinds[ThresholdKindPicker.SelectedIndex]);
        Refresh();
    }

    private void OnThresholdChanged(object? sender, TextChangedEventArgs e)
    {
        if (_applying || _draft is null)
            return;

        // Deliberately not clamped or rewritten while typing: pulling "4" up to the minimum the
        // moment it is typed makes "45" impossible to enter. Validation says what is wrong, and
        // Save is what refuses — including when the field holds no number at all, which the draft
        // remembers so an emptied field cannot save the level it used to show.
        _draft.SetThresholdText(e.NewTextValue);

        PreviewLabel.Text = _draft.Describe();

        var errors = _draft.Validate();
        ValidationLabel.Text = errors.Count > 0 ? errors[0].Message : string.Empty;
        ValidationLabel.IsVisible = errors.Count > 0;
    }

    private void OnPeriodChanged(object? sender, EventArgs e)
    {
        if (_applying || _draft is null || PeriodPicker.SelectedIndex < 0)
            return;

        _draft.SelectPeriod(_draft.Periods[PeriodPicker.SelectedIndex]);
        Refresh();
    }

    private void OnEvaluationPeriodsChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_applying || _draft is null)
            return;

        _draft.SelectEvaluationPeriods((int)e.NewValue);
        Refresh();
    }

    private void OnDatapointsChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_applying || _draft is null)
            return;

        _draft.SelectDatapointsToAlarm((int)e.NewValue);
        Refresh();
    }

    private void OnGateToggled(object? sender, ToggledEventArgs e)
    {
        if (_applying || _draft is null)
            return;

        _draft.Request.ContextGate = e.Value ? AlarmContextGate.Inactive : AlarmContextGate.None;
        Refresh();
    }

    private void OnSeverityChanged(object? sender, EventArgs e)
    {
        if (_applying || _draft is null || SeverityPicker.SelectedIndex < 0)
            return;

        _draft.Request.Severity = SeverityPicker.SelectedIndex switch
        {
            2 => AlertSeverity.Red,
            1 => AlertSeverity.Orange,
            _ => AlertSeverity.Yellow,
        };

        // Changing away from red and back is a fresh decision, so the confirmation is asked again.
        _draft.Request.ConfirmCriticalSeverity = false;
        Refresh();
    }

    private void OnMissingDataChanged(object? sender, EventArgs e)
    {
        if (_applying || _draft is null || MissingDataPicker.SelectedIndex < 0)
            return;

        _draft.Request.MissingDataTreatment = MissingDataPicker.SelectedIndex switch
        {
            1 => AlarmMissingDataTreatment.NotBreaching,
            2 => AlarmMissingDataTreatment.Ignore,
            _ => AlarmMissingDataTreatment.Missing,
        };
        Refresh();
    }

    private async void OnSaveTapped(object? sender, EventArgs e)
    {
        if (_draft is null || _saving)
            return;

        // Red pushes through quiet hours and escalates to other carers. Asked once, at the point
        // of saving, rather than as a checkbox somebody scrolls past.
        if (_draft.NeedsCriticalConfirmation)
        {
            var confirmed = await DisplayAlert(
                "Wake you for this?",
                "An urgent alarm sounds through quiet hours and goes on to other carers if nobody "
                + "acknowledges it. Use it for the things that cannot wait until morning.",
                "Yes, wake me",
                "Pick something quieter");

            if (!confirmed)
                return;

            _draft.Request.ConfirmCriticalSeverity = true;
        }

        var errors = _draft.Validate();
        if (errors.Count > 0)
        {
            ValidationLabel.Text = errors[0].Message;
            ValidationLabel.IsVisible = true;
            return;
        }

        _saving = true;
        SaveButton.IsEnabled = false;
        try
        {
            if (_alarmId is { } id)
                await _api.SaveMemberAlarmAsync(_memberId, id, _draft.Request);
            else
                await _api.CreateMemberAlarmAsync(_memberId, _draft.Request);

            await Shell.Current.GoToAsync("..");
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't save this alarm");
        }
        catch (ApiException)
        {
            // Session gone — the app is already on its way back to sign-in.
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnDeleteTapped(object? sender, EventArgs e)
    {
        if (_alarmId is not { } id || _saving)
            return;

        var reverting = _provenance == AlarmProvenance.Overridden;
        var confirmed = reverting
            ? await DisplayAlert(
                "Go back to the account setting?",
                "This person's own version of this alarm is removed, and the one set for the whole "
                + "account applies to them again.",
                "Use the account setting",
                "Keep theirs")
            : await DisplayAlert(
                "Remove this alarm?",
                "CardiTrack will stop watching for this level. Its own patterns carry on as before.",
                "Remove",
                "Keep it");

        if (!confirmed)
            return;

        _saving = true;
        try
        {
            await _api.DeleteMemberAlarmAsync(_memberId, id);
            await Shell.Current.GoToAsync("..");
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(
                ex.Message, reverting ? "Couldn't go back to the account setting" : "Couldn't remove this alarm");
        }
        catch (ApiException)
        {
            // Session gone.
        }
        finally
        {
            _saving = false;
        }
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync("..");

    // ── labels ───────────────────────────────────────────────────────────────────────────

    private static int IndexOf<T>(IReadOnlyList<T> values, T value)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(values[i], value))
                return i;
        }
        return values.Count > 0 ? 0 : -1;
    }

    private static string StatisticLabel(AlarmStatistic statistic) => statistic switch
    {
        AlarmStatistic.Minimum => "its lowest",
        AlarmStatistic.Maximum => "its highest",
        AlarmStatistic.Average => "an average",
        AlarmStatistic.Sum => "a total",
        _ => "the value",
    };

    private static string ThresholdKindLabel(AlarmThresholdKind kind) => kind switch
    {
        AlarmThresholdKind.BaselinePercent => "a share of what is usual for them",
        AlarmThresholdKind.BaselineSigma => "how much they normally vary",
        _ => "a fixed level",
    };

    private static string PeriodLabel(int minutes) => minutes switch
    {
        AlarmMetricCatalogue.DailyPeriodMinutes => "a whole day",
        60 => "an hour",
        _ => $"{minutes} minutes",
    };

    private static string Readings(int count) => count == 1 ? "reading" : "readings";

    /// <summary>
    /// Display formatting, in the caregiver's own culture so that what the field shows is what the
    /// keypad lets them type back. Invariant here would print "0.5" beside a keypad offering a
    /// comma.
    /// </summary>
    private static string Format(decimal value) =>
        value == decimal.Truncate(value)
            ? ((long)value).ToString(CultureInfo.CurrentCulture)
            : value.ToString("0.#", CultureInfo.CurrentCulture);
}
