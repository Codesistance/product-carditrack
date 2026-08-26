using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

public partial class StatusHeroCard : ContentView
{
    private const string QaPulseAnimation = "qa-pulse";
    private const string AdvisePulseAnimation = "advise-pulse";

    /// <summary>Raised when the card body is tapped — the dashboard's route into M1-13.</summary>
    public event EventHandler? MemberTapped;

    /// <summary>Raised when the weather chip is tapped, carrying the reading it was built from.</summary>
    public event EventHandler<WeatherSnapshotResponse>? WeatherTapped;

    private WeatherSnapshotResponse? _weather;

    /// <summary>
    /// Who and which tier <see cref="Apply"/> last rendered, so a late-arriving
    /// <see cref="ApplyDynamicMessage"/> can tell whether it's still describing the status
    /// actually on screen.
    /// </summary>
    /// <remarks>
    /// The member is half of that identity, not a formality. The dashboard re-resolves its primary
    /// member on every attended refresh, so one page instance can render a different person — and
    /// with the tier alone as the test, a line written about one member survived onto another's
    /// card whenever the two happened to share a tier.
    /// </remarks>
    private Guid _cardiMemberId;
    private string? _healthStatus;

    /// <summary>
    /// The live pair currently on the card, kept so a reload that lands on the same tier can put
    /// it straight back instead of dropping to the static copy — see <see cref="Apply"/>.
    /// </summary>
    private string? _liveHeadline;
    private string? _liveMessage;

    public StatusHeroCard()
    {
        InitializeComponent();
        // Same lifecycle discipline as PendingBotIndicator: a card taken off-screen while a
        // question or a suggestion is still pending must not leave its animation looping in the
        // background.
        Unloaded += (_, _) =>
        {
            StopQaPulse();
            StopAdvisePulse();
        };
    }

    public void Apply(DashboardResponse data)
    {
        var firstName = NameFormatting.FirstName(data.Name);
        NameLabel.Text = $"{data.Name}, {data.Age}";
        Avatar.Apply(data.Name, data.PhotoUrl);
        ApplyAdvise(data.HasAdvise);

        // Headline first, sentence second: the headline is the whole state in three or four
        // words, so a caregiver who reads nothing else has still read the answer.
        (string ColorKey, string? Icon, string? Headline, string Detail) line = data.HealthStatus switch
        {
            "green" => ("StatusGreen", "icon_status_check.svg", "All steady",
                $"{firstName} is doing well"),
            "yellow" => ("StatusYellow", "icon_status_warning.svg", "Something's different",
                $"{firstName}'s day isn't quite following the usual shape"),
            "orange" => ("StatusOrange", "icon_status_urgent.svg", "Worth a check-in",
                $"Today looks off enough that {firstName} is worth a call"),
            "red" => ("StatusRed", "icon_status_critical.svg", "Reach out now",
                $"Something needs attention — contact {firstName}"),
            // Paused is not a health reading — never dress it up as one.
            "paused" => ("StatusUnknown", "icon_status_paused.svg", "Monitoring paused",
                $"We're not collecting data or raising alerts for {firstName}"),
            // No baseline yet is not the same as nothing to say. Rather than tell a caregiver for
            // weeks that we are still getting to know their relative — which reports on us, not on
            // them — the line reads back the day's actual readings. It is the one tier where the
            // honest answer is the numbers themselves.
            //
            // Headline-less on purpose: "Today so far" was a label for the sentence under it, not
            // a reading of how the member is, and every other tier spends that row on the answer.
            // Dropping it gives the sentence the whole block to say something worth reading, and
            // costs nothing — there is no status glyph to earn here either, since the tier's whole
            // point is that no judgement has been made.
            _ => ("StatusUnknown", null, null, TodaySoFar(data.Metrics, firstName)),
        };

        // A reload that lands on the same member and tier keeps the live line the card already
        // earned; one that changes either throws it away, since it described a state no longer on
        // screen. Without this every unattended reload would drop back to the static copy and swap
        // the live line in again a moment later — a flicker nobody asked for on a screen that now
        // reloads itself every 30 seconds.
        if (data.CardiMemberId != _cardiMemberId || data.HealthStatus != _healthStatus)
            ClearLiveStatus();
        else if (_liveMessage is { } live)
            line = (line.ColorKey, line.Icon, _liveHeadline ?? line.Headline, live);

        SetStatusLine(line.ColorKey, line.Icon, line.Headline, line.Detail);
        _cardiMemberId = data.CardiMemberId;
        _healthStatus = data.HealthStatus;

        ApplyWeather(data.Weather);
        ApplyPendingQuestionnaire(data.PendingQuestionnaire);
    }

    /// <summary>
    /// Shows or hides the Q&amp;A button beside the Daybook one, from
    /// <see cref="DashboardResponse.PendingQuestionnaire"/>. Starts the pulse while a question is
    /// waiting, stops it otherwise — <see cref="OnQaTapped"/> is what a caregiver taps into to
    /// answer it.
    /// </summary>
    private void ApplyPendingQuestionnaire(QuestionnaireResponse? pending)
    {
        var hasPending = pending is not null;
        QaCluster.IsVisible = hasPending;

        if (!hasPending)
        {
            StopQaPulse();
            return;
        }

        // Always "1" today — at most one pending question per member (see
        // QuestionnairesPageResponse.Pending) — but a superscript number rather than a dot, so
        // this still reads correctly if that ever stops being true.
        QaBadgeLabel.Text = "1";
        SemanticProperties.SetDescription(QaBorder, "Questions, 1 waiting");
        StartQaPulse();
    }

    /// <summary>
    /// Same construction as <see cref="PendingBotIndicator"/>'s breathing ring, scaled to stay
    /// inside QaCluster's own 42x42 bounds rather than growing past them — this button sits in a
    /// row with Alerts right beside it, with no room for a ring to spill into.
    /// </summary>
    private void StartQaPulse()
    {
        this.AbortAnimation(QaPulseAnimation);

        var pulse = new Animation
        {
            { 0.00, 0.50, new Animation(v => QaPulseRing.Opacity = v, 0.0, 0.6, Easing.SinInOut) },
            { 0.50, 1.00, new Animation(v => QaPulseRing.Opacity = v, 0.6, 0.0, Easing.SinInOut) },
            { 0.00, 0.50, new Animation(v => QaPulseRing.Scale = v, 0.9, 1.05, Easing.SinInOut) },
            { 0.50, 1.00, new Animation(v => QaPulseRing.Scale = v, 1.05, 0.9, Easing.SinInOut) },
        };
        pulse.Commit(this, QaPulseAnimation, length: 1400, repeat: () => IsLoaded && QaCluster.IsVisible);
    }

    private void StopQaPulse()
    {
        this.AbortAnimation(QaPulseAnimation);
        QaPulseRing.Opacity = 0;
    }

    /// <summary>
    /// Shows or hides the Advise button at the end of the row, from
    /// <see cref="DashboardResponse.HasAdvise"/>, and pulses it while a suggestion is waiting.
    /// Hidden outright when there is none: the button's whole job is to open the "Something to try"
    /// card, and that card is itself hidden on Details when nothing was suggested —
    /// a button always on screen would be a dead end most days.
    /// </summary>
    private void ApplyAdvise(bool hasAdvise)
    {
        AdviseCluster.IsVisible = hasAdvise;

        if (!hasAdvise)
        {
            StopAdvisePulse();
            return;
        }

        // Started only when one isn't already running, rather than restarted on every Apply the
        // way QaCluster's is: the dashboard reloads itself every thirty seconds, and each restart
        // snaps the ring back to its first frame — a stutter, on the one thing here whose whole
        // job is to move. Asking the animation rather than tracking the previous visibility is
        // what also makes this self-heal: the Unloaded handler stops the loop, and the reload
        // that follows a caregiver coming back to this tab starts it again.
        if (!this.AnimationIsRunning(AdvisePulseAnimation))
            StartAdvisePulse();
    }

    /// <summary>
    /// The ring blooms out from behind the glyph and fades, the way
    /// <see cref="PendingBotIndicator"/>'s does — but bounded to the button's own 36, since
    /// Alerts sits directly beside it and the card's edge directly after it. 32 at 1.06 is 33.9
    /// across, plus a 2 stroke, which is the widest it can grow without touching either.
    /// </summary>
    private void StartAdvisePulse()
    {
        this.AbortAnimation(AdvisePulseAnimation);

        var pulse = new Animation
        {
            { 0.00, 0.75, new Animation(v => AdvisePulseRing.Scale = v, 0.6, 1.06) },
            { 0.00, 0.15, new Animation(v => AdvisePulseRing.Opacity = v, 0.0, 0.55) },
            { 0.15, 0.75, new Animation(v => AdvisePulseRing.Opacity = v, 0.55, 0.0) },
        };
        pulse.Commit(this, AdvisePulseAnimation, length: 1600,
            repeat: () => IsLoaded && AdviseCluster.IsVisible);
    }

    private void StopAdvisePulse()
    {
        this.AbortAnimation(AdvisePulseAnimation);
        AdvisePulseRing.Opacity = 0;
    }

    /// <summary>Icon-and-temperature chip beside the name. Hidden outright rather than shown
    /// empty — the server sends null unless the member has consented and something was derived.</summary>
    private void ApplyWeather(WeatherSnapshotResponse? weather)
    {
        _weather = weather;
        WeatherChip.IsVisible = weather is not null;
        if (weather is null)
            return;

        WeatherGlyphLabel.Text = WeatherGlyph.For(weather.Condition);
        WeatherTemperatureLabel.Text = weather.TemperatureCelsius is { } temperature
            ? $"{temperature:F0}°C"
            : string.Empty;
    }

    /// <summary>Raised by the card's top-right Daybook button; the page decides the journey.</summary>
    public event EventHandler? DaybookTapped;

    /// <summary>Raised by the card's top-right Alerts button.</summary>
    public event EventHandler? AlertsTapped;

    /// <summary>Raised by the Q&amp;A button, only visible while a question is waiting. The page
    /// decides what "answer it" means — see <see cref="IPopupService.ShowPendingQuestionAsync"/>.</summary>
    public event EventHandler? QaTapped;

    private void OnDaybookTapped(object? sender, TappedEventArgs e) =>
        DaybookTapped?.Invoke(this, EventArgs.Empty);

    private void OnAlertsTapped(object? sender, TappedEventArgs e) =>
        AlertsTapped?.Invoke(this, EventArgs.Empty);

    private void OnQaTapped(object? sender, TappedEventArgs e) =>
        QaTapped?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised by the Advise button, only visible while a suggestion is waiting.
    /// The page decides the journey — see <see cref="CardiMemberDetailPage.AdviseFocus"/>.</summary>
    public event EventHandler? AdviseTapped;

    private void OnAdviseTapped(object? sender, TappedEventArgs e) =>
        AdviseTapped?.Invoke(this, EventArgs.Empty);

    private void OnWeatherTapped(object? sender, TappedEventArgs e)
    {
        if (_weather is { } weather)
            WeatherTapped?.Invoke(this, weather);
    }

    /// <summary>
    /// Forgets the live line, so the next <see cref="Apply"/> renders the tier's static copy. For
    /// the caller that has just learned there is no live message to show after all.
    /// </summary>
    public void ClearLiveStatus() => (_liveHeadline, _liveMessage) = (null, null);

    /// <summary>
    /// Whether the card is already showing a live status line for this member and tier. The
    /// dashboard asks before putting the card into <see cref="ShowStatusLoading"/>, and before
    /// restoring a saved line: a refetch that will almost certainly return the same cached line
    /// should not blank a good line first, and a line about somebody else is not a good line.
    /// </summary>
    public bool HasLiveStatusFor(Guid cardiMemberId, string healthStatus) =>
        cardiMemberId == _cardiMemberId && healthStatus == _healthStatus && _liveMessage is not null;

    /// <summary>
    /// Renders the status block, collapsing the headline row for a tier that has no headline to
    /// show — <see cref="StatusDetailLabel"/> spans both columns, so what is left lines up with
    /// the name above rather than sitting in the glyph's indent.
    /// </summary>
    private void SetStatusLine(string colorKey, string? icon, string? headline, string detail)
    {
        var hasHeadline = !string.IsNullOrWhiteSpace(headline);

        StatusIcon.IsVisible = hasHeadline && icon is not null;
        if (StatusIcon.IsVisible)
            StatusIcon.Source = icon;

        StatusHeadlineLabel.IsVisible = hasHeadline;
        if (hasHeadline)
        {
            StatusHeadlineLabel.TextColor =
                (Color)Microsoft.Maui.Controls.Application.Current!.Resources[colorKey];
            StatusHeadlineLabel.Text = headline;
        }

        StatusDetailLabel.Text = detail;
    }

    /// <summary>
    /// Swaps in the live, MedGemma-generated pair over the static per-tier copy
    /// <see cref="Apply"/> already rendered. Ignored if the card has since moved to a different
    /// member or status — a refresh landing while the call was still in flight — since the message
    /// would describe someone, or something, no longer showing.
    /// </summary>
    /// <param name="headline">
    /// The punchy note. Optional on its own: a generation that produced a sentence but no usable
    /// headline keeps the tier's static headline rather than leaving the row headless.
    /// </param>
    public void ApplyDynamicMessage(
        string? headline, string message, Guid forCardiMemberId, string forHealthStatus)
    {
        if (forCardiMemberId != _cardiMemberId
            || forHealthStatus != _healthStatus
            || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // The server caches this line for minutes at a time, so most ticks re-deliver what is
        // already on the card. Re-fading it would pulse the row every 30 seconds for no change.
        var unchanged = message == _liveMessage
            && (string.IsNullOrWhiteSpace(headline) || headline == _liveHeadline);

        if (!string.IsNullOrWhiteSpace(headline))
        {
            StatusHeadlineLabel.IsVisible = true;
            StatusHeadlineLabel.Text = headline;
            _liveHeadline = headline;
        }
        StatusDetailLabel.Text = message;
        _liveMessage = message;

        if (unchanged)
            return;

        StatusHeadlineLabel.Opacity = 0;
        StatusDetailLabel.Opacity = 0;
        _ = StatusHeadlineLabel.FadeToAsync(1, 150, Easing.CubicOut);
        _ = StatusDetailLabel.FadeToAsync(1, 150, Easing.CubicOut);
    }

    /// <summary>
    /// The day in one sentence, from the readings the dashboard already has — no model call, no
    /// baseline needed. Shown while a member has no established normal to be judged against, which
    /// is exactly when the readings are all there is to report.
    /// </summary>
    /// <remarks>
    /// Reads as a sentence about the member, not a caption listing readings — "so far: 3,442
    /// steps and 70 bpm resting" was telemetry, and this line is the whole of what the card has
    /// to say. It names whose day it is, keeps last night's sleep distinguishable from today's
    /// readings, and lands at 20–25 words with all three in — enough to read naturally, short
    /// enough to wrap inside the column beside the display image without pushing the card past
    /// the fold on a small screen.
    /// </remarks>
    private static string TodaySoFar(DashboardMetrics? metrics, string firstName)
    {
        // Every branch below opens with the subject, so an unnamed member gets a stand-in that
        // reads correctly at the start of a sentence.
        var who = string.IsNullOrWhiteSpace(firstName) ? "This CardiMember" : firstName;

        string? steps = null, heartRate = null, sleep = null;
        if (metrics is not null)
        {
            if (metrics.Steps.Value is { } stepCount)
                steps = $"taken {stepCount:N0} steps";
            if (metrics.RestingHeartRate.Value is { } bpm)
                heartRate = $"a resting heart rate of {bpm:N0} bpm";
            if (metrics.Sleep.Value is { } hours)
            {
                // Pluralized against the rounded display value, not the raw one: 1.04 h renders
                // as "1", and "1 hours of sleep" reads as a bug.
                var rounded = hours.ToString("0.#");
                sleep = $"{rounded} {(rounded == "1" ? "hour" : "hours")} of sleep last night";
            }
        }

        // Today's readings first, joined into one clause: "kept" carries a lone heart rate,
        // since "has a resting heart rate" reads as a diagnosis rather than today's reading.
        var today = (steps, heartRate) switch
        {
            (not null, not null) => $"{steps} with {heartRate}",
            (not null, null) => steps,
            (null, not null) => $"kept {heartRate}",
            _ => null,
        };

        // Last night's sleep stays in its own clause — two different days, so the sentence
        // can't imply the sleep was slept today. The subject leads every branch, so the
        // capitalized "This CardiMember" stand-in never lands mid-sentence.
        return (today, sleep) switch
        {
            (not null, not null) => $"{who} has {today} so far today, after getting {sleep}.",
            (not null, null) => $"{who} has {today} so far today.",
            (null, not null) => $"{who} got {sleep}, but nothing has come in from today yet.",
            _ => $"{who} hasn't sent any readings through yet.",
        };
    }

    /// <summary>
    /// What the card says while the live status line is still being fetched. Only for the tiers
    /// that actually make that call — a member with no reading to interpret is not loading
    /// anything, and would sit on this forever.
    /// </summary>
    public void ShowStatusLoading()
    {
        StatusHeadlineLabel.IsVisible = true;
        StatusHeadlineLabel.Text = "Loading";
        StatusDetailLabel.Text = "Please wait — checking how they're doing.";
    }

    private void OnCardTapped(object? sender, TappedEventArgs e) =>
        MemberTapped?.Invoke(this, EventArgs.Empty);
}
