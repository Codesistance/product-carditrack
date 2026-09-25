using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using CardiTrack.Domain.Entities;

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

    /// <summary>
    /// A member's stored first name — what a family member would actually say aloud — or null
    /// when there is no member or no name, so generated copy falls back to its own wording
    /// ("them") rather than interpolating an empty string.
    /// </summary>
    internal static string? FirstNameOf(CardiMember? member) =>
        string.IsNullOrWhiteSpace(member?.FirstName) ? null : member.FirstName.Trim();

    /// <summary>
    /// The first word of a single full name. For names still held as one string — caregivers'
    /// — never for a CardiMember, whose first name is stored: use <see cref="FirstNameOf"/>.
    /// </summary>
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
    /// Whether a member's name can be redacted against at all. Every slot boundary asks this
    /// before it wraps anything for the Rewrite slot, and refuses in its own way when the answer
    /// is no.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Redact"/> hands the text straight back when the name is null or blank, which is
    /// the right behaviour for a redaction helper and the wrong one for a boundary: a crossing
    /// that proceeds on it sends the clinical read to Vertex unredacted, and that read may repeat
    /// a name out of the caregiver notes <c>DemographicsContextSource</c> decrypts without
    /// redacting. The no-op is silent, and the downstream write guards do not catch it, because
    /// DPIA A20's boundary is about what is <em>sent</em>, not about what is stored.
    /// </para>
    /// <para>
    /// Stated once, here, rather than as a condition each caller invents: the first sweep for
    /// this looked for <c>member?.Name</c> and so classified the five crossings that pass a
    /// non-nullable <c>member.Name</c> as safe — a name that is present but blank fails exactly
    /// the same way.
    /// </para>
    /// </remarks>
    internal static bool CanRedactAgainst([NotNullWhen(true)] string? name) =>
        !string.IsNullOrWhiteSpace(name);

    /// <summary>
    /// Redacts a caregiver's own message for a Rewrite-slot call, refusing the call outright when
    /// there is no name to redact against.
    /// </summary>
    /// <remarks>
    /// The chat-side twin of <see cref="CanRedactAgainst"/>. A caregiver who types "how is Moses"
    /// has put the member's name in the one string these paths send to Vertex, and
    /// <see cref="Redact"/> hands it back untouched when the member row is gone or nameless — a
    /// silent no-op on the DPIA A20 boundary (#1246). Refusing is what the caller would do anyway
    /// a moment later: the turn's write guard answers 404 for a member the product no longer
    /// holds, so this only moves that answer ahead of the model call instead of after it.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">The member has no name on file.</exception>
    internal static string RedactMessageOrRefuse(string message, string? name)
    {
        if (!CanRedactAgainst(name))
            throw new KeyNotFoundException("We couldn't find what you were looking for.");

        return Redact(message, name) ?? message;
    }

    /// <summary>
    /// The inverse of <see cref="Resolve"/>: swaps the member's name out of text that is about to
    /// be sent to a model, so nothing downstream of it ever sees who this is. Pass the member's
    /// full name (<c>CardiMember.FullName</c>). It is matched whole, and so is every leading run of
    /// its words, longest first — "Mary Ann Smith", then "Mary Ann", then "Mary" — so the full
    /// name is replaced whole rather than leaving a bare surname behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why every leading run and not just the first word: the stored first name is always one of
    /// them, and it can be more than one word ("Mary Ann"). It is the stored first name that
    /// <see cref="Resolve"/> writes into chat replies, so history fed back to a model holds it
    /// verbatim. Matching only "Mary" would send "CardiTrackCardiMember Ann" — half a name.
    /// </para>
    /// <para>
    /// Case-insensitive and word-bounded. A member whose name is also an ordinary word ("May",
    /// "Bill") will see that word replaced wherever it appears in the text handed to the model —
    /// accepted deliberately, because the cost is one model call reading slightly odd context
    /// while the cost of the opposite mistake is a real name reaching a third-party provider.
    /// Nothing a caregiver reads passes through here.
    /// </para>
    /// </remarks>
    internal static string? Redact(string? text, string? name)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(name))
            return text;

        // Words, not characters: the gap between two words matches any run of whitespace, so a
        // name stored or written with a double space or a tab ("Mary  Ann") is still matched whole.
        // Rebuilding the forms with single spaces would miss it and leave all but the first word.
        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var redacted = text;
        for (var count = words.Length; count >= 1; count--)
        {
            // A single-letter first name is not worth matching: the word boundary would fire on
            // every stray initial in the text and say nothing about who the member is. The full
            // name is always matched, however short.
            if (count < words.Length && count == 1 && words[0].Length <= 1)
                continue;

            var pattern = string.Join(@"\s+", words.Take(count).Select(Regex.Escape));
            redacted = Regex.Replace(redacted, $@"\b{pattern}\b", Token, RegexOptions.IgnoreCase);
        }

        return redacted;
    }

    /// <summary>
    /// Tolerates case and a separator between the two halves — the shapes a small model actually
    /// returns once it has been asked to write the token into prose. The possessive is left alone
    /// on purpose: "CardiTrackCardiMember's" resolves to "Dad's" by substituting the token and
    /// leaving the apostrophe where the model put it.
    /// </summary>
    /// <remarks>
    /// The lookahead keeps this pattern off <see cref="PronounPlaceholder"/>'s three tokens, which
    /// are built on this one: without it "CardiTrackCardiMemberTheir" resolves to "DadTheir",
    /// which is neither a name nor a pronoun and would reach a caregiver as both. It admits the
    /// same <c>[_-]</c> separators that pattern does, and whitespace for neither reason it does:
    /// "CardiTrackCardiMember their doctor" is the name token followed by an ordinary word far
    /// more often than it is a mangled pronoun token, and swallowing the name there would cost a
    /// caregiver the one word this mechanism exists to produce.
    /// </remarks>
    [GeneratedRegex(
        @"CardiTrack[\s_-]*Cardi[\s_-]*Member(?![_-]*(?:They|Them|Their)\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
}
