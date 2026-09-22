namespace CardiTrack.Domain.Entities;

/// <summary>
/// The human-readable code a family is known by — the thing one sibling reads down the phone to
/// another, and the thing a shared link auto-fills.
/// </summary>
/// <remarks>
/// <para>
/// Eight characters from a 31-letter alphabet in two groups of four, as <c>KTR-7M2Q</c>. The
/// alphabet drops the four pairs people confuse when reading aloud or writing down — <c>I</c> and
/// <c>1</c>, <c>O</c> and <c>0</c> — keeping one of each, so a misheard code fails to resolve
/// rather than resolving to somebody else's family.
/// </para>
/// <para>
/// <strong>Not a secret, and not sized as one.</strong> Roughly 2^39 codes is nowhere near enough
/// to resist a determined search, and it is not meant to be: knowing a Family ID buys the right to
/// ask an admin, which is worth nothing on its own. What defends the family is that approval is
/// mandatory, that a request reveals nothing to the asker, and that guessing is rate-limited. If
/// this were ever the whole authorization for anything, it would need to be a token instead — see
/// <c>InviteTokens</c>, which is what that looks like.
/// </para>
/// </remarks>
public static class FamilyIdentifier
{
    /// <summary>
    /// Unambiguous when spoken or written: no <c>I</c>, <c>O</c>, <c>0</c> or <c>1</c>.
    /// </summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private const int GroupLength = 4;

    /// <summary>A fresh code, in display form.</summary>
    public static string Mint()
    {
        var chars = new char[GroupLength * 2 + 1];
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(GroupLength * 2);

        var at = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i == GroupLength)
                chars[at++] = '-';

            chars[at++] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }

    /// <summary>
    /// The storage form of whatever somebody typed, or null if it could not be a Family ID.
    /// </summary>
    /// <remarks>
    /// Upper-cased and stripped of separators and spaces, so a code read out over a bad line and
    /// typed as "ktr 7m2q" finds the same family as one pasted from a link. Refusing that would
    /// make the product's own suggestion — read it to them — the least reliable way to use it.
    /// </remarks>
    public static string? NormalizeOrNull(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 32)
            return null;

        Span<char> buffer = stackalloc char[GroupLength * 2];
        var at = 0;

        foreach (var c in input)
        {
            if (c is '-' or ' ' or '_')
                continue;

            var upper = char.ToUpperInvariant(c);
            if (!Alphabet.Contains(upper) || at == buffer.Length)
                return null;

            buffer[at++] = upper;
        }

        return at == buffer.Length ? new string(buffer) : null;
    }

    /// <summary>The stored form rendered for a screen: <c>KTR7M2Q9</c> becomes <c>KTR7-M2Q9</c>.</summary>
    public static string ToDisplay(string stored) =>
        stored.Length == GroupLength * 2
            ? $"{stored[..GroupLength]}-{stored[GroupLength..]}"
            : stored;
}
