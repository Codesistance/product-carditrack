using System.Text.RegularExpressions;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Lets generated copy name the person without the model ever being told who they are.
/// </summary>
/// <remarks>
/// <para>
/// The prompts send <see cref="Token"/> where a name belongs and the real name is substituted here,
/// on the way out. That keeps <see cref="MedicalPromptBlocks.MemberContext"/>'s rule intact — "Name
/// and id are deliberately absent — they would identify the member to the model without changing a
/// word of the clinical interpretation" — while still producing copy that says "Dad has been
/// quieter today" rather than "your relative has been quieter today". A caregiver is reading about
/// one specific person; the relationship nouns a nameless prompt forces ("your relative", "your
/// loved one") are the tell that nothing here knows who that is.
/// </para>
/// <para>
/// Matching is deliberately loose. A 4B model reproduces a token approximately: it lowercases it,
/// pads the braces, or possessive-inflects the whole thing. Anything stricter than this would leave
/// literal braces in a caregiver's summary, which is worse than the impersonal phrasing it replaced.
/// </para>
/// </remarks>
internal static partial class NamePlaceholder
{
    /// <summary>
    /// Braces rather than a name-shaped sentinel: a placeholder that looks like a name cannot be
    /// told apart from the model simply using that name, so a leftover would ship unnoticed.
    /// </summary>
    internal const string Token = "{{NAME}}";

    /// <summary>The person's first name — what a family member would actually say aloud.</summary>
    internal static string? FirstName(string? name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : parts[0];
    }

    /// <summary>
    /// True when any recognisable form of the placeholder survives in <paramref name="text"/>.
    /// The caller uses this to refuse to store copy it cannot resolve, rather than showing braces.
    /// </summary>
    internal static bool IsPresentIn(string? text) =>
        !string.IsNullOrEmpty(text) && TokenPattern().IsMatch(text);

    /// <summary>
    /// Replaces every form of the placeholder with <paramref name="name"/>. Returns the text
    /// untouched when either side is missing — a caller with no name to substitute must decide
    /// what to do about that (see <c>DigestGenerationService</c>, which discards the generation),
    /// because silently deleting the token would leave a sentence with a hole in it.
    /// </summary>
    internal static string? Resolve(string? text, string? name)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(name))
            return text;

        return TokenPattern().Replace(text, name);
    }

    /// <summary>
    /// Tolerates case, inner padding, and a single or doubled brace pair — the shapes a small
    /// model actually returns. The possessive is left alone on purpose: "{{NAME}}'s" resolves to
    /// "Dad's" by substituting the token and leaving the apostrophe where the model put it.
    /// </summary>
    [GeneratedRegex(@"\{\{?\s*NAME\s*\}?\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
}
