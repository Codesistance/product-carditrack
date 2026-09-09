using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// The alarms a caregiver has set for this CardiMember — account-level defaults and this member's
/// own, folded into one list.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same shape as <see cref="AlertSettingsPage"/>: a switch saves the moment it is
/// flipped, a failed save puts it back and says so, and while one save is in flight the others
/// refuse to move so two quick flips cannot race. A caregiver who has learned one page should find
/// nothing to learn in the other.
/// </para>
/// <para>
/// Only the primary caregiver can change an alarm. Everyone else sees the same list with the
/// switches disabled and the intro line saying why, so the page never reads as broken.
/// </para>
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(MemberName), "name")]
[QueryProperty(nameof(CanManage), "canManage")]
public partial class MetricAlarmsPage : ContentPage
{
    public const string Route = "metricalarms";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private Guid _memberId;
    private string _memberName = string.Empty;
    private bool _canManage;
    private IReadOnlyList<MetricAlarmResponse>? _alarms;

    /// <summary>Guards Switch.Toggled while we build or roll back rows.</summary>
    private bool _applying;

    /// <summary>Alarm currently waiting on a save — blocks overlapping toggles.</summary>
    private Guid? _toggleInFlight;

    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;

    public MetricAlarmsPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        _feedback = new RefreshFeedback(SavedBanner, Updating);
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
            _memberName = Uri.UnescapeDataString(value ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(_memberName))
                HeaderSubtitle.Text = $"Alerts you set yourself for {_memberName}";
        }
    }

    /// <summary>
    /// Whether the caregiver may change an alarm. Member Details already knows, so it rides along
    /// on the route rather than costing this page a second member fetch.
    /// </summary>
    public string CanManage
    {
        set => _canManage = bool.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var can) && can;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // A popup closing raises this again. Coming back from the builder should reload, though —
        // the list is stale by then — so a returning page clears its cache before navigating.
        if (_popups.IsShowing || _alarms is not null)
            return;

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_gate.IsLoading)
            return;
        var ticket = _gate.Begin();
        var memberId = _memberId;

        try
        {
            // Alarms the caregiver set themselves: the saved list goes up at once on a landing
            // with nothing on screen, and the live one confirms it behind. A toggle that just
            // succeeded evicts the key (see the client), so the reload it triggers is live.
            var outcome = await SnapshotRefresh.RunAsync(
                _api, _gate, ticket,
                peek: _alarms is null ? ct => _api.PeekMemberAlarmsAsync(memberId, ct) : null,
                fetch: ct => _api.GetMemberAlarmsAsync(memberId, ct),
                render: alarms =>
                {
                    _alarms = alarms;
                    Render(alarms);
                },
                _feedback,
                sameAs: SamePayload.Same);

            // The list has to go when there is nothing behind it, not just be covered. This page
            // reloads — returning from the builder clears the cache, and a successful toggle
            // calls this directly — so a failure here can land on top of a list that is already
            // rendered. What it must never do is leave stale alarms up unmarked; a saved-only
            // outcome is marked by the banner, which is the honest version of the same thing.
            if (outcome.Result == RefreshResult.NothingAndFailed)
            {
                _alarms = null;
                AlarmsPanel.IsVisible = false;
                ErrorDetailLabel.Text = outcome.Error!.Message;
                LoadingSpinner.IsVisible = false;
                LoadingSpinner.IsRunning = false;
                ErrorPanel.IsVisible = true;
            }
        }
        finally
        {
            _gate.Release(ticket);
        }
    }

    private void Render(IReadOnlyList<MetricAlarmResponse> alarms)
    {
        LoadingSpinner.IsVisible = false;
        LoadingSpinner.IsRunning = false;
        ErrorPanel.IsVisible = false;
        AlarmsPanel.IsVisible = true;
        AddButton.IsVisible = _canManage;

        IntroLabel.Text = _canManage
            ? "CardiTrack watches for its own patterns already. However, you may have some specific concerns of your own."
            : "CardiTrack watches for its own patterns already. The primary carer can add specific concerns of their own here.";

        var enabled = alarms.Count(a => a.IsEnabled);
        CrowdingNotice.IsVisible = enabled > MetricAlarmValidation.RecommendedMaxEnabledAlarms;
        CrowdingLabel.Text =
            $"{enabled} custom alerts are switched on. Past about {MetricAlarmValidation.RecommendedMaxEnabledAlarms} "
            + "it gets hard to hold them all in mind, and alerts nobody can account for are the ones that end up ignored.";

        EmptyPanel.IsVisible = alarms.Count == 0;
        // An empty host still takes its slot in the stack, so the intro sat two spacings above
        // the empty-state card instead of one. Hidden until there is a row to hold.
        AlarmsHost.IsVisible = alarms.Count > 0;

        _applying = true;
        try
        {
            AlarmsHost.Clear();
            foreach (var alarm in alarms)
                AlarmsHost.Add(BuildCard(alarm));
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// One alarm as a card: its name, what it watches in one sentence, where it came from and how
    /// loud it is, and its switch. Tapping the card opens the builder.
    /// </summary>
    private View BuildCard(MetricAlarmResponse alarm)
    {
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;

        var title = new Label
        {
            Text = alarm.Name,
            Style = (Style)resources["Body1SemiBoldDark"],
            FontSize = 15,
            LineBreakMode = LineBreakMode.WordWrap,
        };

        var condition = new Label
        {
            // Composed server-side. Re-deriving it here would let this screen and the alert the
            // alarm raises describe the same condition in two different ways.
            Text = alarm.Condition,
            Style = (Style)resources["Body2"],
            FontSize = 13,
            LineBreakMode = LineBreakMode.WordWrap,
        };

        var pills = new HorizontalStackLayout { Spacing = 6 };
        pills.Add(SeverityPill(alarm.Severity, resources));
        if (alarm.Provenance is AlarmProvenance.Inherited or AlarmProvenance.Overridden)
            pills.Add(Pill(alarm.Provenance == AlarmProvenance.Inherited ? "From your account" : "Tuned for them", resources));
        if (alarm.State == AlarmEvaluationState.InsufficientData)
            pills.Add(Pill("Waiting for data", resources));

        var textStack = new VerticalStackLayout
        {
            Spacing = 4,
            VerticalOptions = LayoutOptions.Center,
            Children = { title, condition, pills },
        };

        var toggle = new Switch
        {
            IsToggled = alarm.IsEnabled,
            IsEnabled = _canManage,
            OnColor = (Color)resources["Primary"],
            VerticalOptions = LayoutOptions.Start,
        };
        toggle.Toggled += (_, args) => OnToggled(alarm, toggle, args.Value);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) },
            ColumnSpacing = 12,
            MinimumHeightRequest = 48,
        };
        grid.Add(textStack, 0);
        grid.Add(toggle, 1);
        SemanticProperties.SetDescription(grid, $"{alarm.Name}. {alarm.Condition}");

        var card = new Border
        {
            Style = (Style)resources["ElevatedCard"],
            Content = grid,
        };

        if (_canManage)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await OpenBuilderAsync(alarm);
            // On the text only: tapping near the switch should flip it, not navigate away from it.
            textStack.GestureRecognizers.Add(tap);
        }

        return card;
    }

    private async void OnToggled(MetricAlarmResponse alarm, Switch toggle, bool enabled)
    {
        if (_applying)
            return;

        if (_toggleInFlight is not null)
        {
            // Another save is in flight — put the switch back and wait.
            _applying = true;
            toggle.IsToggled = !enabled;
            _applying = false;
            return;
        }

        var previous = !enabled;
        _toggleInFlight = alarm.Id;
        toggle.IsEnabled = false;
        try
        {
            // Switching an inherited alarm off writes this member an override that is off — which
            // is what an opt-out is. The server takes the account alarm's id and works that out.
            await _api.SaveMemberAlarmAsync(_memberId, alarm.Id, ToRequest(alarm, enabled));

            // Reload rather than trust the row we hold. The server may have answered with a
            // different row — switching an opt-out back on puts the account default back, under
            // the default's own id — and the count in the crowding notice has moved either way.
            // A list left as it was would send the next tap at an id that no longer exists.
            await LoadAsync();
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            _applying = true;
            toggle.IsToggled = previous;
            _applying = false;
            await _popups.ShowErrorAsync(ex.Message, "Couldn't update this alert");
        }
        catch (ApiException)
        {
            // Session gone — the app is already on its way back to sign-in.
        }
        finally
        {
            _toggleInFlight = null;
            toggle.IsEnabled = _canManage;
        }
    }

    private static SaveMetricAlarmRequest ToRequest(MetricAlarmResponse alarm, bool enabled) => new()
    {
        Name = alarm.Name,
        Metric = alarm.Metric,
        Statistic = alarm.Statistic,
        Operator = alarm.Operator,
        ThresholdKind = alarm.ThresholdKind,
        ThresholdValue = alarm.ThresholdValue,
        PeriodMinutes = alarm.PeriodMinutes,
        EvaluationPeriods = alarm.EvaluationPeriods,
        DatapointsToAlarm = alarm.DatapointsToAlarm,
        MissingDataTreatment = alarm.MissingDataTreatment,
        Severity = alarm.Severity,
        ContextGate = alarm.ContextGate,
        IsEnabled = enabled,

        // Re-saving an alarm that is already red is not a new decision to make it red, and asking
        // again every time a caregiver flips its switch would train them to tap past the warning.
        ConfirmCriticalSeverity = alarm.Severity == AlertSeverity.Red,
    };

    private static View Pill(string text, ResourceDictionary resources) => new Border
    {
        Style = (Style)resources["StatusPill"],
        BackgroundColor = (Color)resources["PillNeutralBackground"],
        Content = new Label { Text = text, Style = (Style)resources["StatusPillText"] },
    };

    private static View SeverityPill(AlertSeverity severity, ResourceDictionary resources)
    {
        var (background, label) = severity switch
        {
            AlertSeverity.Red => ("PillRedBackground", "Urgent"),
            AlertSeverity.Orange => ("PillOrangeBackground", "Worth a look"),
            _ => ("PillYellowBackground", "Good to know"),
        };

        return new Border
        {
            Style = (Style)resources["StatusPill"],
            BackgroundColor = (Color)resources[background],
            Content = new Label { Text = label, Style = (Style)resources["StatusPillText"] },
        };
    }

    private async void OnAddTapped(object? sender, EventArgs e) => await OpenBuilderAsync(null);

    private async Task OpenBuilderAsync(MetricAlarmResponse? alarm)
    {
        // The list is stale the moment the builder saves, and OnAppearing short-circuits on a
        // cached one. Clearing here is what makes coming back show the change.
        _alarms = null;

        var route = $"{MetricAlarmEditPage.Route}?memberId={_memberId}"
            + $"&name={Uri.EscapeDataString(_memberName)}";
        if (alarm is not null)
            route += $"&alarmId={alarm.Id}";

        await Shell.Current.GoToAsync(route);
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(
            $"{AppShell.DashboardRoute}/{CardiMemberDetailPage.Route}?memberId={_memberId}");
}
