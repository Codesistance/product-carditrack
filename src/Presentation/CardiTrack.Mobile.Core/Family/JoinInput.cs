using CardiTrack.Domain.Entities;

namespace CardiTrack.Mobile.Core.Family;

/// <summary>
/// What a caregiver typed or pasted into the "join" field, sorted into the two things it can be.
/// </summary>
/// <remarks>
/// D-11: a Family ID is one secret with two ways in — typed, or carried by a link that fills it
/// in. A caregiver invitation is a different object (a token minted for one member) but it
/// arrives through the same door, a link somebody sent them, so one field takes both and this
/// decides which it was. A link is read for its query only; nothing about its host is trusted,
/// because the same value pasted bare has to mean the same thing.
/// </remarks>
public abstract record JoinInput
{
    /// <summary>A caregiver invitation's token — from <c>…/join?t=…</c> or pasted on its own.</summary>
    public sealed record Invitation(string Token) : JoinInput;

    /// <summary>A Family ID, normalised to the eight stored characters.</summary>
    public sealed record FamilyCode(string FamilyId) : JoinInput;

    private const int MinimumTokenLength = 16;

    public static JoinInput? Parse(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme)
            && uri.Scheme is "http" or "https" or "carditrack")
        {
            return FromQuery(uri.Query);
        }

        if (FamilyIdentifier.NormalizeOrNull(trimmed) is { } familyId)
            return new FamilyCode(familyId);

        return LooksLikeToken(trimmed) ? new Invitation(trimmed) : null;
    }

    private static JoinInput? FromQuery(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split <= 0)
                continue;
            var key = Uri.UnescapeDataString(pair[..split]);
            var value = Uri.UnescapeDataString(pair[(split + 1)..]).Trim();

            if (string.Equals(key, "t", StringComparison.OrdinalIgnoreCase) && LooksLikeToken(value))
                return new Invitation(value);

            if (string.Equals(key, "family", StringComparison.OrdinalIgnoreCase)
                && FamilyIdentifier.NormalizeOrNull(value) is { } familyId)
            {
                return new FamilyCode(familyId);
            }
        }

        return null;
    }

    /// <summary>
    /// Base64url, as <c>InviteTokens.Mint</c> produces, and long enough that a Family ID — eight
    /// letters, and already claimed above — can never be mistaken for one.
    /// </summary>
    private static bool LooksLikeToken(string value) =>
        value.Length >= MinimumTokenLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
