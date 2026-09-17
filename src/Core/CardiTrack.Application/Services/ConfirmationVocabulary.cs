namespace CardiTrack.Application.Services;

/// <summary>
/// The closed yes/no list every two-turn chat mutation shares: alert-settings apply and
/// journal discard/rewrite. One vocabulary so a caregiver's "yes please" cannot mean
/// confirm on one rung and a new question on the other.
/// </summary>
/// <remarks>
/// Exact match after punctuation is stripped and digits are kept. "yes but only at night"
/// and "yes 130" are new instructions, not consent. Erring narrow is the safe direction:
/// a yes that slips through routes normally and the offer lapses unapplied.
/// </remarks>
public static class ConfirmationVocabulary
{
    public static ConfirmationAnswer? Read(string message)
    {
        var kept = new string(message.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '\'')
            .ToArray());
        var normalised = string.Join(' ', kept.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (normalised.Length == 0)
            return null;
        if (YesPhrases.Contains(normalised))
            return ConfirmationAnswer.Yes;
        if (NoPhrases.Contains(normalised))
            return ConfirmationAnswer.No;
        return null;
    }

    public static bool IsAffirmative(string message) => Read(message) == ConfirmationAnswer.Yes;

    public static bool IsNegative(string message) => Read(message) == ConfirmationAnswer.No;

    private static readonly HashSet<string> YesPhrases = new(StringComparer.Ordinal)
    {
        "yes", "y", "yep", "yeah", "yup", "yes please", "please", "ok", "okay", "sure", "go ahead",
        "go on", "do it", "please do", "confirm", "confirmed", "yes do it", "do that", "fine",
        "sounds good", "that's right", "thats right", "correct", "proceed", "make it so", "yes thanks",
        "yes thank you", "ok do it", "okay do it", "ok go ahead", "okay go ahead", "go for it",
        "yes go ahead", "yes proceed", "alright", "all right",
    };

    private static readonly HashSet<string> NoPhrases = new(StringComparer.Ordinal)
    {
        "no", "n", "nope", "nah", "no thanks", "no thank you", "cancel", "don't", "dont", "do not",
        "leave it", "never mind", "nevermind", "stop", "not now", "no leave it", "forget it",
        "leave things as they are", "leave it as it is", "no don't", "no dont",
    };
}
