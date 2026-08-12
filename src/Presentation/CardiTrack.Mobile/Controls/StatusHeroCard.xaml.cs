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
        var (colorKey, icon, headline, detail) = data.HealthStatus switch
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
            _ => ("StatusUnknown", "icon_status_check.svg", "Today so far",
                TodaySoFar(data.Metrics, firstName)),
        };

        var color = (Color)Microsoft.Maui.Controls.Application.Current!.Resources[colorKey];
        StatusIcon.Source = icon;
        StatusHeadlineLabel.TextColor = color;
        StatusHeadlineLabel.Text = headline;
        StatusDetailLabel.Text = detail;
        _healthStatus = data.HealthStatus;
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

        if (!string.IsNullOrWhiteSpace(headline))
            StatusHeadlineLabel.Text = headline;
        StatusDetailLabel.Text = message;

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
    private static string TodaySoFar(DashboardMetrics? metrics, string firstName)
    {
        if (metrics is null)
            return $"Waiting on {firstName}'s first readings";

        var parts = new List<string>();
        if (metrics.Steps.Value is { } steps)
            parts.Add($"{steps:N0} steps");
        if (metrics.RestingHeartRate.Value is { } heartRate)
            parts.Add($"{heartRate:N0} bpm resting");
        if (metrics.Sleep.Value is { } sleep)
            parts.Add($"{sleep:0.#} h sleep");

        return parts.Count == 0
            ? $"Waiting on {firstName}'s first readings"
            : string.Join(", ", parts);
    }

    /// <summary>
    /// What the card says while the live status line is still being fetched. Only for the tiers
    /// that actually make that call — a member with no reading to interpret is not loading
    /// anything, and would sit on this forever.
    /// </summary>
    public void ShowStatusLoading()
    {
        StatusHeadlineLabel.Text = "Loading";
        StatusDetailLabel.Text = "Please wait — checking how they're doing.";
    }

    private void OnCardTapped(object? sender, TappedEventArgs e) =>
        MemberTapped?.Invoke(this, EventArgs.Empty);
}
