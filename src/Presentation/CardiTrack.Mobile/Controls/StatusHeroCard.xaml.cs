using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

public partial class StatusHeroCard : ContentView
{
    /// <summary>Raised when the card body is tapped — the dashboard's route into M1-13.</summary>
    public event EventHandler? MemberTapped;

    /// <summary>
    /// Which tier <see cref="Apply"/> last rendered, so a late-arriving
    /// <see cref="ApplyDynamicMessage"/> can tell whether it's still describing the status
    /// actually on screen.
    /// </summary>
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
    }

    public void Apply(DashboardResponse data)
    {
        var firstName = NameFormatting.FirstName(data.Name);
        NameLabel.Text = $"{data.Name}, {data.Age}";
        Avatar.Apply(data.Name, data.PhotoUrl);

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

        // A reload that lands on the same tier keeps the live line the card already earned; one
        // that changes tier throws it away, since it described a status no longer on screen.
        // Without this every unattended reload would drop back to the static copy and swap the
        // live line in again a moment later — a flicker nobody asked for on a screen that now
        // reloads itself every 30 seconds.
        if (data.HealthStatus != _healthStatus)
            ClearLiveStatus();
        else if (_liveMessage is { } live)
            line = (line.ColorKey, line.Icon, _liveHeadline ?? line.Headline, live);

        SetStatusLine(line.ColorKey, line.Icon, line.Headline, line.Detail);
        _healthStatus = data.HealthStatus;
    }

    /// <summary>
    /// Forgets the live line, so the next <see cref="Apply"/> renders the tier's static copy. For
    /// the caller that has just learned there is no live message to show after all.
    /// </summary>
    public void ClearLiveStatus() => (_liveHeadline, _liveMessage) = (null, null);

    /// <summary>
    /// Whether the card is already showing a live status line for <paramref name="healthStatus"/>.
    /// The dashboard asks before putting the card into <see cref="ShowStatusLoading"/>: a refetch
    /// that will almost certainly return the same cached line should not blank a good line first.
    /// </summary>
    public bool HasLiveStatusFor(string healthStatus) =>
        healthStatus == _healthStatus && _liveMessage is not null;

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
    /// status — a refresh landing while the call was still in flight — since the message would
    /// describe a tier that's no longer showing.
    /// </summary>
    /// <param name="headline">
    /// The punchy note. Optional on its own: a generation that produced a sentence but no usable
    /// headline keeps the tier's static headline rather than leaving the row headless.
    /// </param>
    public void ApplyDynamicMessage(string? headline, string message, string forHealthStatus)
    {
        if (forHealthStatus != _healthStatus || string.IsNullOrWhiteSpace(message))
            return;

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
    /// A sentence rather than the bare comma-separated list this used to be. The list was a
    /// caption under a "Today so far" heading; with that heading gone, this line is the whole of
    /// what the card has to say, so it names whose day it is and keeps last night's sleep
    /// distinguishable from today's readings — which a flat list of three numbers did not.
    /// Deliberately still short: it wraps inside the column beside the display image, and a
    /// paragraph there would push the card past the fold on a small screen.
    /// </remarks>
    private static string TodaySoFar(DashboardMetrics? metrics, string firstName)
    {
        // Every branch below opens with the subject, so an unnamed member gets a stand-in that
        // reads correctly at the start of a sentence.
        var who = string.IsNullOrWhiteSpace(firstName) ? "This CardiMember" : firstName;

        // Today's readings and last night's are two different days — grouped separately so the
        // sentence can't imply the sleep was slept today.
        var today = new List<string>(2);
        string? lastNight = null;
        if (metrics is not null)
        {
            if (metrics.Steps.Value is { } steps)
                today.Add($"{steps:N0} steps");
            if (metrics.RestingHeartRate.Value is { } heartRate)
                today.Add($"{heartRate:N0} bpm resting");
            if (metrics.Sleep.Value is { } sleep)
                lastNight = $"{sleep:0.#} h of sleep last night";
        }

        return (today.Count, lastNight) switch
        {
            (> 0, not null) => $"{who}'s day so far: {JoinReadings(today)}, after {lastNight}.",
            (> 0, null) => $"{who}'s day so far: {JoinReadings(today)}.",
            (0, not null) => $"{who} got {lastNight} — nothing in from today yet.",
            _ => $"{who} hasn't sent any readings through yet.",
        };
    }

    /// <summary>"1,729 steps and 70 bpm resting" — two readings at most, so no Oxford comma case.</summary>
    private static string JoinReadings(List<string> readings) => string.Join(" and ", readings);

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
