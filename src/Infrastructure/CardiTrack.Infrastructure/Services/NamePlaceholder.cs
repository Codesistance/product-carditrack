using System.Text.RegularExpressions;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Lets generated copy name the person without any model ever being told who they are. The swap
/// runs both ways: <see cref="Redact"/> puts <see cref="Token"/> where a real name appears in
/// text on its way to a model, and <see cref="Resolve"/> puts the name back in what the model
/// returns.
/// </summary>
/// <remarks>
/// <para>
/// Outbound, the prompts send <see cref="Token"/> where a name belongs. That keeps
/// <see cref="MedicalPromptBlocks.MemberContext"/>'s rule intact — "Name and id are deliberately
/// absent — they would identify the member to the model without changing a word of the clinical
/// interpretation" — while still producing copy that says "Dad has been quieter today" rather
/// than "your relative has been quieter today". A caregiver is reading about one specific person;
/// the relationship nouns a nameless prompt forces ("your relative", "your loved one") are the
/// tell that nothing here knows who that is.
/// </para>
/// <para>
/// <b>Inbound matters just as much, and used not to be handled.</b> Member chat persists each
/// reply <em>after</em> resolution, so stored turns hold the member's real first name beside
/// their readings — and that history is fed back into later prompts as context. The name the
/// prompts were so careful never to send was arriving anyway, one turn later, by the back door.
/// <see cref="Redact"/> closes that: any text that has been through resolution goes back out with
/// the name swapped for the token again.
/// </para>
/// <para>
/// Redaction applies to prompt text only — never to what is stored or shown. A caregiver's
/// conversation keeps the real name on screen and at rest; only the copy handed to a model is
/// rewritten. That is what makes case-insensitive matching the right call below: over-redacting
/// costs a model slightly odd context for one call, while under-redacting leaks a name to a
/// third-party provider.
/// </para>
/// </remarks>
internal static partial class NamePlaceholder
{
    /// <summary>
    /// A single, unmistakable word rather than the <c>{{NAME}}</c> braces this used to use.
    /// Braces were chosen so a leftover could never be mistaken for the model simply using a
    /// name; that reasoning does not survive the swap going both ways. Nobody is called
    /// CardiTrackCardiMember, so a leftover is every bit as obvious, while a word-shaped token
    /// buys two things braces cannot: a 4B model inflects it into a sentence correctly instead of
    /// mangling the punctuation (it lowercased them, padded them, doubled them — hence the loose
    /// pattern this file has always needed), and redacted history reads to the model in exactly
    /// the vocabulary its own output is asked to use.
    /// </summary>
    internal const string Token = "CardiTrackCardiMember";

    /// <summary>The person's first name — what a family member would actually say aloud.</summary>
    internal static string? FirstName(string? name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : parts[0];
    }

    /// <summary>
    /// True when any recognisable form of the placeholder survives in <paramref name="text"/>.
    /// The caller uses this to refuse to store copy it cannot resolve, rather than showing the
    /// sentinel to a caregiver.
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
    /// The inverse of <see cref="Resolve"/>: swaps the member's name out of text that is about to
    /// be sent to a model, so nothing downstream of it ever sees who this is. Both the full name
    /// and the first name are matched, longest first, so "Margaret Doe" is replaced whole rather
    /// than leaving a bare surname behind.
    /// </summary>
    /// <remarks>
    /// Case-insensitive and word-bounded. A member whose name is also an ordinary word ("May",
    /// "Bill") will see that word replaced wherever it appears in the text handed to the model —
    /// accepted deliberately, because the cost is one model call reading slightly odd context
    /// while the cost of the opposite mistake is a real name reaching a third-party provider.
    /// Nothing a caregiver reads passes through here.
    /// </remarks>
    internal static string? Redact(string? text, string? name)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(name))
            return text;

        var forms = new List<string> { name.Trim() };
        // A single-letter first name is not worth matching: the word boundary would fire on
        // every stray initial in the text and say nothing about who the member is.
        if (FirstName(name) is { Length: > 1 } first
            && !forms.Contains(first, StringComparer.OrdinalIgnoreCase))
        {
            forms.Add(first);
        }

        var redacted = text;
        foreach (var form in forms.OrderByDescending(f => f.Length))
            redacted = Regex.Replace(redacted, $@"\b{Regex.Escape(form)}\b", Token, RegexOptions.IgnoreCase);

        return redacted;
    }

    /// <summary>
    /// Tolerates case and a separator between the two halves — the shapes a small model actually
    /// returns once it has been asked to write the token into prose. The possessive is left alone
    /// on purpose: "CardiTrackCardiMember's" resolves to "Dad's" by substituting the token and
    /// leaving the apostrophe where the model put it.
    /// </summary>
    [GeneratedRegex(@"CardiTrack[\s_-]*Cardi[\s_-]*Member", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
}
