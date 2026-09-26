using CardiTrack.Domain.Enums;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

public partial class StatusHeroCard : ContentView
{
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
        NameBlock.SizeChanged += (_, _) => FitName();
    }

    /// <summary>
    /// Caps the name at the width its column leaves once the pin beside it is paid for. The name
    /// line is a stack so the pin can follow the name however long it is, and a stack measures its
    /// children without a width limit — uncapped, a long name would run past the card instead of
    /// truncating.
    /// </summary>
    private void FitName()
    {
        var available = NameBlock.Width;
        if (available <= 0)
            return;

        // The pin's cell lays out at its face's width, 26: the rest of its 44 is tap band given
        // back by its negative margin (see the XAML).
        const double PinFace = 26;
        NameLabel.MaximumWidthRequest = PinButton.IsVisible
            ? Math.Max(0, available - PinFace - NameLine.Spacing)
            : available;
    }

    public void Apply(DashboardResponse data)
    {
        var firstName = data.DisplayFirstName();
        NameLabel.Text = data.DisplayFirstName();
        // "66 years - Male". Unspecified is left off rather than spelled out: a caregiver who
        // did not answer the question does not need it read back to them under the name.
        var sex = data.Gender switch
        {
            Gender.Male => "Male",
            Gender.Female => "Female",
            _ => null,
        };
        MemberMetaLabel.Text = sex is null
            ? $"{data.Age} years"
            : $"{data.Age} years · {sex}";
        Avatar.Apply(data.Name, data.PhotoUrl);

        // Headline first, sentence second: the headline is the whole state in three or four
        // words, so a caregiver who reads nothing else has still read the answer.
        (string ColorKey, string? Icon, string? Headline, string Detail) line = data.HealthStatus switch
        {
            "green" => ("StatusGreen", "icon_status_info_green.svg", "All steady",
                $"{firstName} is doing well"),
            "yellow" => ("StatusYellow", "icon_status_info_yellow.svg", "Something's different",
                $"{firstName}'s day isn't quite following the usual shape"),
            "orange" => ("StatusOrange", "icon_status_info_orange.svg", "Worth a check-in",
                $"Today looks off enough that {firstName} is worth a call"),
            "red" => ("StatusRed", "icon_status_info_red.svg", "Reach out now",
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
    }

    // The card's news — alerts, the CardiJournal, "Something to try", a waiting question and the
    // no-device warning — moved to the card's foot on 2026-09-26 and their logic with them: see
    // MemberDashboardCard.ApplyNews. The hero is now who the member is and how they are doing.

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

    /// <summary>Raised when the caregiver taps the pin; the dashboard owns the pins and the order.</summary>
    public event EventHandler? PinTapped;

    /// <summary>
    /// Shows the pin after the name — the dashboard does while several members stack — and whether
    /// this member is pinned, as the filled glyph and for a screen reader.
    /// </summary>
    public void SetPinning(bool available, bool pinned)
    {
        PinButton.IsVisible = available;
        PinIcon.Source = pinned ? "icon_pin_on.svg" : "icon_pin.svg";
        SemanticProperties.SetDescription(PinButton, pinned ? "Unpin from the top" : "Pin to the top");
        FitName();
    }

    private void OnPinTapped(object? sender, TappedEventArgs e) => PinTapped?.Invoke(this, EventArgs.Empty);

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
    /// show, which leaves the sentence alone lining up with the name above it.
    /// </summary>
    /// <remarks>
    /// <c>MemberStatusLine.Icon</c> is drawn again, in its own column ahead of the headline. It
    /// was dropped when this block sat in a narrow column beside the avatar and the glyph left
    /// the headline starting at a different x from the sentence under it; the block spans the
    /// card now, and the sentence is inset to meet the headline's text rather than its glyph.
    /// </remarks>
    private void SetStatusLine(string colorKey, string? icon, string? headline, string detail)
    {
        var hasHeadline = !string.IsNullOrWhiteSpace(headline);

        StatusHeadlineLabel.IsVisible = hasHeadline;
        if (hasHeadline)
        {
            StatusHeadlineLabel.TextColor =
                (Color)Microsoft.Maui.Controls.Application.Current!.Resources[colorKey];
            StatusHeadlineLabel.Text = headline;
        }

        StatusIcon.IsVisible = hasHeadline && !string.IsNullOrWhiteSpace(icon);
        if (StatusIcon.IsVisible)
            StatusIcon.Source = icon;

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
