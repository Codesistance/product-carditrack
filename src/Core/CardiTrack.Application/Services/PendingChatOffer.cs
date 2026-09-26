using System.Text.Json;
using System.Text.Json.Serialization;
using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.Services;

/// <summary>
/// A follow-up the chat offered at the end of an answer and is waiting on a yes for: another
/// reading over the same days. Written by code from <see cref="For"/>, never by a model, and kept
/// on the offering turn so a bare "yes" has something to mean.
/// </summary>
/// <remarks>
/// <para>
/// Exists because a model-written offer could not be taken up (2026-09-26). The rewrite closed
/// answers with "shall I look at…?" in free prose that nothing stored; the caregiver's "yes" carried
/// no topic, the router sees questions and never replies, and the yes was answered with a greeting.
/// Here the offer is a closed pair, a metric and a window, and a yes turns it into a question code
/// writes (<see cref="Question"/>) that routes like any other. The router still never reads a
/// model's prose.
/// </para>
/// <para>
/// Only what the chat can actually fetch is offered: another chartable metric over the same days,
/// at most the whitelist's seven. "Further back" is never offered, because chat reads a week at
/// most by design (<see cref="DataQueryWhitelist"/>).
/// </para>
/// <para>
/// Honoured only while it is the most recent assistant turn and inside <see cref="Validity"/>,
/// the same window as <see cref="PendingAlertChange"/>. Serialised by name for the reason that
/// record gives; anything that does not parse is no offer.
/// </para>
/// </remarks>
public sealed record PendingChatOffer
{
    /// <summary>How long after offering a yes still takes the offer up.</summary>
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(15);

    /// <summary>The longest window an offer can name — the chat's own fetch ceiling.</summary>
    public const int MaxDays = DataQueryWhitelist.MaxRecentActivityDays;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// What each answered metric offers next — the reading a caregiver most often wants beside it.
    /// Sleep and resting heart rate point at each other; the rest point at whichever of the two
    /// they are read alongside on the dashboard. A metric missing here is offered nothing.
    /// </summary>
    private static readonly IReadOnlyDictionary<ChartMetricKind, ChartMetricKind> NextReading =
        new Dictionary<ChartMetricKind, ChartMetricKind>
        {
            [ChartMetricKind.Sleep] = ChartMetricKind.RestingHeartRate,
            [ChartMetricKind.RestingHeartRate] = ChartMetricKind.Sleep,
            [ChartMetricKind.Steps] = ChartMetricKind.Sleep,
            [ChartMetricKind.HeartRateVariability] = ChartMetricKind.RestingHeartRate,
            [ChartMetricKind.OvernightBreathingRate] = ChartMetricKind.Sleep,
        };

    /// <summary>The reading on offer.</summary>
    public required ChartMetricKind Metric { get; init; }

    /// <summary>The days the offered look covers — the same days the answer read.</summary>
    public required int Days { get; init; }

    public required DateTime OfferedAtUtc { get; init; }

    /// <summary>
    /// The offer to make after an answer about <paramref name="answered"/> over
    /// <paramref name="days"/> days, or null when there is none.
    /// </summary>
    public static PendingChatOffer? For(ChartMetricKind answered, int days, DateTime utcNow) =>
        days is >= 1 and <= MaxDays && NextReading.TryGetValue(answered, out var next)
            ? new PendingChatOffer { Metric = next, Days = days, OfferedAtUtc = utcNow }
            : null;

    public bool IsCurrent(DateTime utcNow) => utcNow - OfferedAtUtc <= Validity;

    /// <summary>
    /// The offer as the caregiver reads it, closing the answer. <paramref name="firstName"/> is
    /// the member's resolved first name; the offer names them rather than a pronoun, since the
    /// pronoun rules that hold for model prose do not run on code-written copy.
    /// </summary>
    public string Sentence(string firstName)
    {
        var span = Days == 1 ? "the same day" : "the same days";
        return Metric switch
        {
            ChartMetricKind.Sleep => $"Would you like me to look at how {firstName} slept over {span} too?",
            ChartMetricKind.RestingHeartRate => $"Would you like me to look at {firstName}'s resting heart rate over {span} too?",
            _ => throw new InvalidOperationException($"No offer is written for {Metric}."),
        };
    }

    /// <summary>
    /// The question a yes turns this offer into, naming the member as <paramref name="subject"/>
    /// — the name placeholder, since this goes to the models exactly as a typed question would.
    /// </summary>
    public string Question(string subject)
    {
        var span = Days switch
        {
            1 => "over the last day",
            MaxDays => "this week",
            _ => $"over the last {Days} days",
        };
        return Metric switch
        {
            ChartMetricKind.Sleep => $"How has {subject} slept {span}?",
            ChartMetricKind.RestingHeartRate => $"How has {subject}'s resting heart rate been {span}?",
            _ => throw new InvalidOperationException($"No offer is written for {Metric}."),
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The stored offer, or null when there is none to honour: absent, unreadable, or
    /// not one <see cref="For"/> could have made.</summary>
    public static PendingChatOffer? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        PendingChatOffer? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PendingChatOffer>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        return parsed is not null
            && parsed.Days is >= 1 and <= MaxDays
            && NextReading.Values.Contains(parsed.Metric)
            ? parsed
            : null;
    }
}
