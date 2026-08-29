using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Common;

namespace CardiTrack.Mobile.Core.Journal;

/// <summary>
/// What the journal settings screen offers for the four comparison tolerances, and how each
/// chosen value is said back to a caregiver.
/// </summary>
/// <remarks>
/// <para>
/// In Core for the reason <see cref="JournalLabels"/> gives: the MAUI project cannot be unit
/// tested, and the arguable part of these rows is not the layout but which values are offered and
/// what the words for them are. The page below is left holding taps and a save.
/// </para>
/// <para>
/// Every list is read from the response the server sent, never invented here — the ladder is
/// published (<c>JournalSettingsResponse.SelectableToleranceMinutes</c>) precisely so a client
/// cannot drift from it, the same stance the book timings take on their window and step. The
/// fallbacks below are for a response that predates the field, not a licence to choose.
/// </para>
/// </remarks>
public static class JournalComparisonChoices
{
    /// <summary>The clock tolerances to offer, as the caregiver reads them.</summary>
    public static IReadOnlyList<string> ToleranceOptions(JournalSettingsResponse settings) =>
        Rungs(settings.SelectableToleranceMinutes, JournalComparison.SelectableToleranceMinutes)
            .Select(ToleranceLabel)
            .ToList();

    /// <inheritdoc cref="ToleranceOptions"/>
    public static IReadOnlyList<string> DirectionBoundOptions(JournalSettingsResponse settings) =>
        Rungs(settings.SelectableDirectionBoundMinutes, JournalComparison.SelectableDirectionBoundMinutes)
            .Select(DirectionBoundLabel)
            .ToList();

    /// <summary>
    /// The level band, in plain words. The percentage stays behind the label: a caregiver is
    /// choosing how much small movement they want mentioned, not reasoning about a share of a
    /// thirty-day mean.
    /// </summary>
    public static IReadOnlyList<string> LevelToleranceOptions(JournalSettingsResponse settings) =>
        Rungs(settings.SelectableLevelTolerancePercents, JournalComparison.SelectableLevelTolerancePercents)
            .Select(LevelToleranceLabel)
            .ToList();

    /// <summary>
    /// A tolerance as a row's value — "20 min", and "Every minute" at zero, which is what a
    /// tolerance of nothing actually means rather than the absence of a setting.
    /// </summary>
    public static string ToleranceLabel(int minutes) =>
        minutes == 0
            ? "Every minute"
            : string.Create(CultureInfo.CurrentCulture, $"{minutes} min");

    /// <summary>
    /// A direction bound as a row's value. Said in hours past the hour mark, because "360 min" is
    /// a number a reader has to convert before it means anything about a night.
    /// </summary>
    public static string DirectionBoundLabel(int minutes)
    {
        if (minutes < 60)
            return string.Create(CultureInfo.CurrentCulture, $"{minutes} min");

        var hours = minutes / 60m;
        var remainder = minutes % 60;

        if (remainder != 0)
            return string.Create(CultureInfo.CurrentCulture, $"{minutes / 60}h {remainder}m");

        return hours == 1m
            ? "1 hour"
            : string.Create(CultureInfo.CurrentCulture, $"{hours:0.#} hours");
    }

    /// <summary>
    /// The level band in plain words, with its percentage in brackets so the label is honest about
    /// what it sets without leading with it.
    /// </summary>
    /// <remarks>
    /// "Mention every difference" rather than "None" at zero: the setting's effect is what a
    /// caregiver is picking, and "None" reads as the feature being switched off when it is in fact
    /// at its most talkative.
    /// </remarks>
    public static string LevelToleranceLabel(decimal percent) => percent switch
    {
        <= 0m => "Mention every difference",

        // The two named rungs match exactly, not by range. The ladder is offerable rather than
        // enforced — the server takes any value inside the bounds at one decimal place — so a
        // range would have read a stored 1.5% back as "Ignore small ones (2%)" and shown the
        // caregiver a figure that is not their setting. Anything off the ladder falls through and
        // is said in full.
        1m => "Ignore slight ones (1%)",
        2m => "Ignore small ones (2%)",

        _ => string.Create(CultureInfo.CurrentCulture, $"Ignore anything under {percent:0.#}%"),
    };

    /// <summary>
    /// The minutes behind a label this class produced, or null when the text is not one of them —
    /// a cancelled sheet, or an option list from a newer server than this build knows.
    /// </summary>
    public static int? ToleranceFor(JournalSettingsResponse settings, string? label) =>
        MatchOn(
            Rungs(settings.SelectableToleranceMinutes, JournalComparison.SelectableToleranceMinutes),
            ToleranceLabel,
            label);

    /// <inheritdoc cref="ToleranceFor"/>
    public static int? DirectionBoundFor(JournalSettingsResponse settings, string? label) =>
        MatchOn(
            Rungs(settings.SelectableDirectionBoundMinutes, JournalComparison.SelectableDirectionBoundMinutes),
            DirectionBoundLabel,
            label);

    /// <inheritdoc cref="ToleranceFor"/>
    public static decimal? LevelToleranceFor(JournalSettingsResponse settings, string? label) =>
        MatchOn(
            Rungs(settings.SelectableLevelTolerancePercents, JournalComparison.SelectableLevelTolerancePercents),
            LevelToleranceLabel,
            label);

    /// <summary>
    /// The chosen value read back out of the label the caregiver tapped, rather than out of the
    /// sheet's index. An index would break silently the day the ladder gains a rung; a label that
    /// no longer matches anything comes back null and the row is left alone.
    /// </summary>
    private static T? MatchOn<T>(IReadOnlyList<T> rungs, Func<T, string> label, string? chosen)
        where T : struct
    {
        if (string.IsNullOrEmpty(chosen))
            return null;

        foreach (var rung in rungs)
        {
            if (string.Equals(label(rung), chosen, StringComparison.Ordinal))
                return rung;
        }

        return null;
    }

    /// <summary>
    /// The server's ladder, or the compiled-in one when a response predates the field. Never an
    /// empty picker: a row that opens onto nothing reads as broken rather than as unset.
    /// </summary>
    private static IReadOnlyList<T> Rungs<T>(IReadOnlyList<T>? published, IReadOnlyList<T> fallback) =>
        published is { Count: > 0 } ? published : fallback;
}
