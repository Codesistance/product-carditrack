using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Alerts;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Core.Charts;
using CardiTrack.Mobile.Core.Members;
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
    private bool _returningFromPopup;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private AlertDetailResponse? _alert;

    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;

    public AlertDetailPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        _feedback = new RefreshFeedback(SavedBanner, Updating);
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
        if (_gate.IsLoading || _alertId == Guid.Empty)
            return;
        var ticket = _gate.Begin();
        var alertId = _alertId;

        if (_alert is null)
            SetState(loading: true);

        try
        {
            // The device's saved copy of this alert goes up first on a landing with nothing on
            // screen; the live one replaces it under the overlay. The 30-second tick and a pull
            // already have the alert up and replace it in place.
            var outcome = await SnapshotRefresh.RunAsync(
                _api, _gate, ticket,
                peek: _alert is null ? ct => _api.PeekAlertAsync(alertId, ct) : null,
                fetch: ct => _api.GetAlertAsync(alertId, ct),
                render: alert =>
                {
                    _alert = alert;
                    Apply(alert);
                    SetState(loaded: true);
                },
                _feedback);

            switch (outcome.Result)
            {
                case RefreshResult.Superseded:
                    return;
                case RefreshResult.NothingAndFailed:
                    // Nothing to show — or a 404 over a snapshot: the alert was deleted
                    // elsewhere, and its saved copy must not outlive it.
                    _alert = null;
                    ErrorDetailLabel.Text = outcome.Error!.Message;
                    SetState(error: true);
                    return;
            }

            if (outcome.IsFresh)
                _lastLoadedUtc = DateTime.UtcNow;
            else if (!silent && outcome.Error is not null)
                await _popups.ShowWarningAsync(outcome.Error.Message, "Couldn't refresh");
        }
        finally
        {
            // Only the load that still owns the screen clears the pull spinner. A superseded one
            // stopping it would take the spinner off a pull that is still running.
            if (_gate.IsCurrent(ticket))
                Refresher.IsRefreshing = false;
            _gate.Release(ticket);
        }
    }

    private void Apply(AlertDetailResponse alert)
    {
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var firstName = alert.MemberFirstName();
        ChatBot.MemberId = alert.CardiMemberId;
        ChatBot.MemberFirstName = firstName;

        // The badge word and the banner fill are both read off severity — see AlertSeverityLook for
        // why yellow says NOTICE and green keeps its own colour. The colour is our own severity
        // scale rather than Figma's blue INFO chip, as on AlertListCard, so a badge can never
        // disagree with the rail beside it.
        var (badge, bannerKey) = AlertSeverityLook.For(alert.Severity);

        SeverityBanner.BackgroundColor = (Color)resources[bannerKey];
        MemberSeverityRail.BackgroundColor = (Color)resources[bannerKey];
        SeverityBadge.Text = badge;
        // On the white member card now, so it wears the banner's colour rather than white on it.
        SeverityBadge.TextColor = (Color)resources[bannerKey];
        ReasonIcon.Source = ReasonIconFor(alert.Reason);
        TitleLabel.Text = alert.Title;
        TimeLabel.Text = FormatWhen(alert);

        Avatar.BoxWidth = 52;
        Avatar.Apply(alert.CardiMemberName, alert.CardiMemberPhotoUrl);
        MemberNameLabel.Text = string.IsNullOrWhiteSpace(alert.MemberFirstName())
            ? "CardiMember"
            : alert.MemberFirstName();
        MemberTypeLabel.Text = alert.Type;
        MessageLabel.Text = alert.Message;

        ApplyChart(alert);
        ApplyComparison(alert.Comparison, alert.Severity);
        ApplyContext(alert, firstName);
        ApplyEvidence(alert);
        ApplyNarrative(alert);
        ApplyResponses(alert);
        ApplyAcknowledgement(alert);

        QuickActions.Apply(
            new QuickActionTarget(
                alert.CardiMemberId,
                alert.MemberFirstName(),
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

    private void ApplyChart(AlertDetailResponse alert)
    {
        var chart = alert.Chart;
        if (chart is null || chart.Series.Count < 2)
        {
            ChartCard.IsVisible = false;
            return;
        }

        ChartCard.IsVisible = true;
        ChartNameLabel.Text = chart.Name;
        ChartWindowLabel.Text = chart.WindowLabel;
        ChartValueLabel.Text = AlertChartKey.Headline(chart);
        ChartValueDayLabel.Text = chart.ValueLabel ?? string.Empty;
        ChartValueDayLabel.IsVisible = !string.IsNullOrWhiteSpace(chart.ValueLabel);
        ChartPartialDayLabel.Text = chart.PartialDayLabel ?? string.Empty;
        ChartPartialDayLabel.IsVisible = !string.IsNullOrWhiteSpace(chart.PartialDayLabel);
        // The still stretch is a movement reading and breathing is a lungs one; giving either the
        // heart fallback told a caregiver the wrong organ — and under the mislabelled "bpm"
        // headline, read as heart-rate-deduced inactivity detection. Blood oxygen (the low-oxygen
        // alert's chart) got the same heart fallback until it had an entry of its own.
        ChartIcon.Source = chart.Metric switch
        {
            "steps" or "longestSedentaryStretch" => "icon_metric_steps.svg",
            "sleep" => "icon_metric_sleep.svg",
            "overnightBreathingRate" => "icon_metric_breathing.svg",
            "spo2" => "icon_metric_spo2.svg",
            _ => "icon_metric_heart.svg",
        };

        var inkKey = chart.Metric switch
        {
            "steps" or "longestSedentaryStretch" => "MetricStepsInk",
            "sleep" => "MetricSleepInk",
            "overnightBreathingRate" => "MetricBreathingInk",
            "spo2" => "MetricSpO2Ink",
            _ => "MetricHeartInk",
        };
        var ink = MetricStatus.Resource(inkKey, Colors.Gray);

        // A sleep chart's zeros are awake nights — see AlertChartKey.Series.
        var series = AlertChartKey.Series(chart);
        var values = series.Where(p => p.Value is not null).Select(p => (double)p.Value!).ToList();
        if (values.Count == 0)
        {
            ChartCard.IsVisible = false;
            return;
        }

        var baseline = chart.Baseline is { } b ? (double)b : (double?)null;
        var reference = AlertChartKey.Reference(chart);
        // Both lines get a say in the extent, on the same rule and in the same order of priority
        // the dashboard's trend card uses, so the band cannot end up drawn off a chart it exists
        // to be read against.
        var scale = TrendScale.For(
            values.Min(),
            values.Max(),
            baseline,
            reference is not null ? (double)reference.Low : null,
            reference is not null ? (double)reference.High : null);

        // Same tap-to-inspect the member-detail, chat and journal charts already give. This page
        // was the one host that drew the points and then ignored a tap on them.
        Chart.Interactive = true;
        Chart.ValueFormatter = v => AlertChartKey.Value(chart, (decimal)v);
        var flagged = AlertChartKey.FlaggedDates(chart, alert.AboutDate);
        var flagColor = flagged.Count == 0 ? null : MetricStatus.Accent(alert.Severity);
        SemanticProperties.SetHint(Chart, flagged.Count == 0
            ? "Tap a reading to see its value"
            : "Tap a reading to see its value. The coloured point is the day this alert is about.");
        Chart.Render(
            series,
            scale,
            ink,
            showMarkers: series.Count <= MarkerPointLimit,
            baseline: chart.Baseline,
            reference: reference,
            flaggedDates: flagged,
            flagColor: flagColor);

        var key = AlertChartKey.For(chart);
        ChartBaselineLabel.IsVisible = key is not null;
        ChartBaselineLabel.Text = key ?? string.Empty;
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
            "green" => ("PillGreenBackground", "StatusGreen"),
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
            // The rule's message names no clock time on purpose; this is where the stored instant
            // becomes one, so a caregiver can tell an afternoon in a chair from an evening they
            // settled early. The ask is the composer's — the page only localises the clock.
            "daytime_inactivity_block" when alert.StretchStartedAt is { } startedAt =>
                StillStretchLine(
                    startedAt, alert.TypicalBedtime, alert.StillStretchAsk, alert.StretchStartedLabel),
            _ => null,
        };

        ContextCard.IsVisible = copy is not null;
        ContextLabel.Text = copy ?? string.Empty;
    }

    /// <summary>
    /// Why this alert came through: the rule's own yardstick, and the line the reading crossed.
    /// </summary>
    /// <remarks>
    /// Rendered from the server's composed sentences rather than assembled here. The wording has
    /// to match what the API tells an integrator and what the compliance record claims the product
    /// tells a caregiver, and three copies of a sentence is three chances for one of them to drift.
    /// The whole card hides when the server sent none — an alert this build cannot explain
    /// honestly shows nothing, because a card that shrugs still reads as a claim.
    /// </remarks>
    private void ApplyEvidence(AlertDetailResponse alert)
    {
        var evidence = alert.Evidence;
        EvidenceCard.IsVisible = evidence is not null;
        if (evidence is null)
            return;

        // The rule's name on the alert-settings screen, with the window its "usual" was learned
        // over where there is one: the caregiver can then find the toggle that silences this, and
        // knows how much history is behind the comparison.
        EvidenceRuleLabel.Text = evidence.BaselinePeriodDays is { } days
            ? $"{evidence.RuleLabel} · measured against their last {days} days"
            : evidence.RuleLabel;
        EvidenceRuleLabel.IsVisible = !string.IsNullOrWhiteSpace(EvidenceRuleLabel.Text);

        EvidenceWhyLabel.Text = evidence.WhyLine;

        EvidenceThresholdChip.IsVisible = !string.IsNullOrWhiteSpace(evidence.ThresholdLabel);
        EvidenceThresholdLabel.Text = evidence.ThresholdLabel ?? string.Empty;
    }

    /// <summary>
    /// The model's reading of the alert, written by the pass that raised it. Absent until that
    /// pass has run, which for an alert opened seconds after it arrived is the normal case — the
    /// card appears on the next refresh rather than the screen waiting for it.
    /// </summary>
    private void ApplyNarrative(AlertDetailResponse alert)
    {
        var narrative = alert.Narrative;
        NarrativeCard.IsVisible = narrative is not null
            && !string.IsNullOrWhiteSpace(narrative.Explanation);
        if (!NarrativeCard.IsVisible)
            return;

        NarrativeLabel.Text = narrative!.Explanation;

        NarrativeActionLabel.IsVisible = !string.IsNullOrWhiteSpace(narrative.RecommendedAction);
        NarrativeActionLabel.Text = narrative.RecommendedAction ?? string.Empty;
    }

    private static string StillStretchLine(
        DateTime startedAtUtc, string? typicalBedtime, string? composedAsk, string? startedLabel)
    {
        // Prefer the member-zone label the server composed with the ask. The phone clock
        // is only a fallback for payloads that predate those fields.
        TimeOnly? bedtime = TimeOnly.TryParse(typicalBedtime, out var parsed) ? parsed : null;
        var local = DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc).ToLocalTime();
        var clock = startedLabel
            ?? local.ToString("h:mm tt");
        var ask = composedAsk ?? AlertDetailComposer.StillStretchAsk(
            TimeOnly.FromDateTime(local), bedtime);
        return $"The still stretch began around {clock} — {ask}.";
    }

    /// <summary>
    /// The handled state, its undo, and the close beside it. Acknowledged offers Undo; resolved
    /// does not — resolution is a judgement that the condition has passed, and the endpoint
    /// refuses to reopen it, so offering a button that would come back with an error would be a
    /// worse answer than not offering one. Close has no undo either, and for the same reason it
    /// is final for caregivers: it re-arms the rule, so taking it back would mean un-firing an
    /// alert that may already have fired again.
    /// </summary>
    /// <summary>
    /// Lays the visible action buttons side by side in equal columns, primary first and Remove
    /// last — [Acknowledge][Close][Remove] while open, [Close][Undo][Remove] once acknowledged,
    /// Remove alone once resolved. Rebuilt from what is visible so a hidden button never leaves
    /// an empty column in the row.
    /// </summary>
    private void PackActionRow(bool acknowledged)
    {
        Button[] order = acknowledged
            ? [CloseButton, UndoAcknowledgeButton, AcknowledgeButton, RemoveButton]
            : [AcknowledgeButton, CloseButton, UndoAcknowledgeButton, RemoveButton];
        var shown = order.Where(button => button.IsVisible).ToList();

        ActionRow.ColumnDefinitions.Clear();
        for (var column = 0; column < shown.Count; column++)
        {
            ActionRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(shown[column], column);
        }
    }

    private void ApplyAcknowledgement(AlertDetailResponse alert)
    {
        var acknowledged = alert.Status == "acknowledged";
        var handled = acknowledged || alert.Status == "resolved";

        AcknowledgeButton.IsVisible = !handled;
        AcknowledgeButton.Text = alert.Severity == "red" ? "I'm on my way" : "Acknowledge";
        UndoAcknowledgeButton.IsVisible = acknowledged;

        // Closing stays available on an alert somebody has acknowledged — that is the ordinary
        // sequence, one caregiver says they are on it and then says what happened — and goes when
        // it is resolved, which is what closed means.
        CloseButton.IsVisible = alert.Status != "resolved";

        // Order says what comes next: acknowledge first while nobody has, close first once
        // somebody has — the next real step either way.
        PackActionRow(acknowledged);

        if (!handled)
        {
            HandledStrip.IsVisible = false;
            return;
        }

        AcknowledgedLabel.Text = AlertAnswerCopy.HandledLine(alert) ?? AlertAnswerCopy.SettledOnItsOwn;
        HandledStrip.BackgroundColor = (Color)Microsoft.Maui.Controls.Application.Current!.Resources[
            AlertAnswerCopy.IsClosed(alert) ? "HandledStripBackground" : "AcknowledgedStripBackground"];
        HandledStrip.IsVisible = true;
    }

    /// <summary>
    /// "What the family did": every response kept against this alert, newest first, each with
    /// who, when, the code's label and the note.
    /// </summary>
    /// <remarks>
    /// The whole of the coordination answer (D-20). There is no in-app feed — nothing reads one
    /// back, and the PRD records that it was not built — so the alert itself is where a family
    /// finds out what somebody else already did about it.
    /// </remarks>
    private void ApplyResponses(AlertDetailResponse alert)
    {
        var responses = AlertAnswerCopy.NewestFirst(alert.Responses);
        ResponsesSection.IsVisible = AlertAnswerCopy.HistoryAddsToTheStrip(responses);
        ResponsesHost.Clear();
        if (!ResponsesSection.IsVisible)
            return;

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        for (var i = 0; i < responses.Count; i++)
        {
            if (i > 0)
            {
                ResponsesHost.Add(new BoxView
                {
                    HeightRequest = 1,
                    Color = (Color)resources["Divider"],
                    Margin = new Thickness(12, 0),
                });
            }

            var response = responses[i];
            var block = new VerticalStackLayout
            {
                Spacing = 2,
                Padding = new Thickness(12, 12),
            };
            block.Add(new Label
            {
                Text = $"{AlertAnswerCopy.RowTitle(response)} · {RelativeTime.Format(response.CreatedAt)}",
                Style = (Style)resources["Body1SemiBoldDark"],
                LineBreakMode = LineBreakMode.WordWrap,
            });
            block.Add(new Label
            {
                Text = AlertAnswerCopy.RowDetail(response),
                Style = (Style)resources["Body2"],
                LineBreakMode = LineBreakMode.WordWrap,
            });
            ResponsesHost.Add(block);
        }
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

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
    }

    /// <summary>
    /// Acknowledging goes through the response page rather than straight to the endpoint: with a
    /// second caregiver, "I am on it" is worth saying in words the rest of the family can read,
    /// and the page's canned chips make that one tap rather than a sentence to compose. The
    /// bodyless acknowledge is still what the endpoint receives when they pick nothing.
    /// </summary>
    private async void OnAcknowledgeClicked(object? sender, EventArgs e)
    {
        if (_alert is not { } alert)
            return;

        await Shell.Current.GoToAsync(
            $"{AlertRespondPage.Route}?alertId={alert.AlertId}&kind={AlertAnswerKinds.Wire(AlertAnswerKind.Acknowledge)}");
    }

    private async void OnCloseClicked(object? sender, EventArgs e)
    {
        if (_alert is not { } alert)
            return;

        await Shell.Current.GoToAsync(
            $"{AlertRespondPage.Route}?alertId={alert.AlertId}&kind={AlertAnswerKinds.Wire(AlertAnswerKind.Close)}");
    }

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

    /// <summary>
    /// Removes the alert from the caregiver's lists — the same housekeeping the Alerts list offers
    /// on a swipe, offered again here because the screen a caregiver opened to decide about an
    /// alert is where they finish deciding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Confirmed first: there is no undo, and unlike acknowledging — which this page lets them
    /// take back on the row above — a removal cannot be walked back from the app at all.
    /// </para>
    /// <para>
    /// On success it leaves for the Alerts list rather than staying: the page is a view of a thing
    /// that no longer belongs on any list, and refreshing in place would either redraw an alert the
    /// caregiver just removed or blank the screen under them. It names the list explicitly rather
    /// than popping, for the reason <see cref="AppShell.AlertsRoute"/> gives — a pop lands wherever
    /// they happened to arrive from, which is the Alerts list only by coincidence and is the
    /// dashboard or a tapped notification often enough to matter.
    /// </para>
    /// <para>
    /// A 404 is treated as success. It means the row is already gone — another caregiver removed
    /// it, or an earlier attempt wrote before its response was lost — and the outcome they asked
    /// for has happened either way. Every other failure keeps them on the page with the alert
    /// intact, because a card that vanished on an error the caregiver never saw is the worse half
    /// of the two.
    /// </para>
    /// </remarks>
    private async void OnDeleteAlertTapped(object? sender, EventArgs e)
    {
        if (_alert is not { } alert)
            return;

        var confirmed = await _popups.ConfirmWarningAsync(
            "This removes the alert from your list — it can't be undone.",
            "Remove this alert?", "Remove", "Cancel");
        if (!confirmed)
            return;

        try
        {
            await _api.DeleteAlertAsync(alert.AlertId);
        }
        catch (ApiException ex) when (ex.IsNotFound)
        {
            // Already gone — that is the outcome they asked for.
        }
        catch (ApiException ex)
        {
            // The expired session included: the shared handler turns that into its own journey,
            // and a warning first would put a popup in front of a sign-in screen.
            if (!ex.IsSessionExpired)
                await _popups.ShowWarningAsync(ex.Message, "Couldn't remove it");
            return;
        }

        await this.GoBackAsync(AppShell.AlertsRoute);
    }

    private async void OnViewActivityDataTapped(object? sender, TappedEventArgs e)
    {
        if (_alert is { } alert)
            await Shell.Current.GoToAsync($"{CardiMemberDetailPage.Route}?memberId={alert.CardiMemberId}");
    }

    /// <summary>
    /// Hands the alert to whatever the caregiver already uses to talk to people. Deliberately the
    /// same three facts the sharer can see on the banner in front of them — who, what, and when —
    /// and none of the numbers below it: this leaves the app for an arbitrary destination, so it
    /// carries the least that still makes the message worth sending.
    /// </summary>
    /// <remarks>
    /// The row says "Share", not "Share with Family". Family is now a place in this app with
    /// people in it, and a row that named it while handing the alert to the OS share sheet
    /// promised something the app does: telling the family is answering the alert, which is the
    /// button below this list.
    /// </remarks>
    private async void OnShareWithFamilyTapped(object? sender, TappedEventArgs e)
    {
        if (_alert is not { } alert)
            return;

        var firstName = alert.MemberFirstName();
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
