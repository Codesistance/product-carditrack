using System.Text.RegularExpressions;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Lets family-facing copy use the right pronoun for the member without any model being told
/// which one that is. The same trick <see cref="NamePlaceholder"/> plays with the name: the
/// prompt asks for a token, and code puts the word back on the way out.
/// </summary>
/// <remarks>
/// <para>
/// Sex reaches a prompt through <see cref="MedicalPromptBlocks.MemberContext"/>, and the member
/// context goes only to the clinical slot. Every brief a caregiver actually reads is written by
/// the Rewrite slot, which receives a <c>DeidentifiedFindings</c> and nothing else — DPIA row
/// A20 — so <see cref="MedicalPromptBlocks.Pronouns"/>'s first clause, "use he or she as the sex
/// given indicates", named a fact that brief is never given. What reached families instead was
/// whichever pronoun the clinical read happened to leave in its prose for the rewrite to copy:
/// on 2026-09-11 one member's summary card said "he" and "his" while the suggestion card
/// directly beneath it said "them", about the same person, on the same screen. The "he" was not
/// even wrong — the member is on file as Male — but nothing in the chain that wrote it knew that.
/// </para>
/// <para>
/// A pronoun the model guesses is a claim about a real person that the product cannot stand
/// behind. Every member created before M1-04 asked for sex sits at
/// <see cref="Gender.PreferNotToSay"/>, and there the guess is a coin toss a family would be
/// right to be upset by. So the decision moves to the one place that holds the answer: the
/// database. The model writes <see cref="Subject"/>, <see cref="Object"/> or
/// <see cref="Possessive"/>, and <see cref="Resolve"/> turns them into he/him/his, she/her/her,
/// or — when sex is not stated — the member's own name, which is exactly the fallback
/// <see cref="MedicalPromptBlocks.Pronouns"/>'s remark asks for in prose and could only request.
/// </para>
/// <para>
/// Nothing new crosses the A20 boundary to make this work. The sex is never sent; a word is
/// substituted into the reply after it comes back, in the same pass that resolves the name.
/// </para>
/// </remarks>
internal static partial class PronounPlaceholder
{
    /// <summary>
    /// The subject pronoun's stand-in — "he" or "she", or the name when sex is not stated.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="NamePlaceholder.Token"/> so the three read as one family to a model
    /// that has just been told to write the name token, and so a leftover is as obviously not a
    /// word as the name token is. The suffixes are the neutral pronouns themselves rather than
    /// grammatical terms: a 4B model asked to write "CardiTrackCardiMemberPossessive" has to know
    /// what a possessive is, while one asked to write "CardiTrackCardiMemberTheir" only has to
    /// know where "their" would have gone.
    /// </remarks>
    internal const string Subject = NamePlaceholder.Token + "They";

    /// <summary>The object pronoun's stand-in — "him" or "her", or the name.</summary>
    internal const string Object = NamePlaceholder.Token + "Them";

    /// <summary>The possessive's stand-in — "his" or "her", or the name with an apostrophe-s.</summary>
    internal const string Possessive = NamePlaceholder.Token + "Their";

    /// <summary>
    /// True when any recognisable form of a pronoun token survives in <paramref name="text"/>.
    /// Callers pair it with <see cref="NamePlaceholder.IsPresentIn"/> and refuse to store copy
    /// that still carries either, rather than showing a sentinel to a caregiver.
    /// </summary>
    /// <remarks>
    /// Deliberately looser than <see cref="TokenPattern"/>, which decides what may be replaced.
    /// This one decides what counts as a leftover, and those are different questions: a token the
    /// resolver refuses — "CardiTrackCardiMemberThey're", where replacing the token alone would
    /// leave "here" — is exactly the case a caller most needs told about. A presence check
    /// narrower than the replacement it guards would report "nothing left to resolve" about a
    /// sentence with the sentinel still in it.
    /// </remarks>
    internal static bool IsPresentIn(string? text) =>
        !string.IsNullOrEmpty(text) && TokenPresencePattern().IsMatch(text);

    /// <summary>
    /// Replaces every pronoun token with the word <paramref name="gender"/> calls for, falling
    /// back to <paramref name="firstName"/> when sex is not stated. Returns the text untouched
    /// when there is nothing to substitute — sex not stated and no name on file — so the caller's
    /// <see cref="IsPresentIn"/> check can discard the copy rather than have this quietly delete
    /// the tokens and leave a sentence with a hole in it, which is the stance
    /// <see cref="NamePlaceholder.Resolve"/> takes for the same case.
    /// </summary>
    /// <remarks>
    /// Runs before the name is resolved. <see cref="NamePlaceholder"/>'s pattern would otherwise
    /// match the <see cref="NamePlaceholder.Token"/> these tokens are built from and leave
    /// "DadTheir" behind; its pattern now refuses these three suffixes outright, so the two are
    /// order-independent, but the order is still stated at each call site because a reader should
    /// not have to know about a lookahead in another file to see why it is safe.
    /// </remarks>
    internal static string? Resolve(string? text, Gender gender, string? firstName)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        if (WordsFor(gender, firstName) is not { } words)
            return text;

        // Every word this resolves to is singular — he, she, or the name — but the token is spelled
        // "They", and a model writing it reaches for the verb that goes with "they": "than
        // CardiTrackCardiMemberThey typically do" became "than he typically do" in front of a
        // caregiver (2026-09-24). The auxiliaries are fixed here, before the swap, while the token
        // still marks exactly which verbs belong to the member; a lexical verb ("they walk") has
        // no rule code can apply safely, so the brief asks for the singular form as well.
        text = SubjectVerbPattern().Replace(text, match =>
            match.Groups["lead"].Value + Singular(match.Groups["verb"].Value));

        return TokenPattern().Replace(text, match =>
        {
            var word = match.Groups["form"].Value.ToUpperInvariant() switch
            {
                "THEY" => words.Subject,
                "THEM" => words.Object,
                _ => words.Possessive,
            };

            return StartsASentence(text, match.Index) ? Capitalise(word) : word;
        });
    }

    /// <summary>
    /// The three words this member's copy should use, or null when none can be settled — sex not
    /// stated and no name on file.
    /// </summary>
    /// <remarks>
    /// The not-stated case repeats the name, including an apostrophe-s for the possessive, which
    /// is the only form that needs building rather than choosing. It reads a little more
    /// insistently than a pronoun would, and that is the trade
    /// <see cref="MedicalPromptBlocks.Pronouns"/>'s remark already made in words: "they" is a
    /// stranger's word for a family reading about one specific person, and repeating the name is
    /// the lesser wrong. Anything else here would be this file inventing the sex the prompt was
    /// careful not to.
    /// </remarks>
    private static (string Subject, string Object, string Possessive)? WordsFor(
        Gender gender, string? firstName) => gender switch
    {
        Gender.Male => ("he", "him", "his"),
        Gender.Female => ("she", "her", "her"),
        _ when !string.IsNullOrWhiteSpace(firstName) =>
            (firstName!.Trim(), firstName.Trim(), $"{firstName.Trim()}'s"),
        _ => null,
    };

    /// <summary>
    /// Whether the token at <paramref name="index"/> opens a sentence, and so needs a capital.
    /// </summary>
    /// <remarks>
    /// The model writes one token whatever the position, so the case has to be decided here. A
    /// newline counts: a bulleted or wrapped line starts a sentence whether or not the line above
    /// it ended in a full stop.
    /// </remarks>
    private static bool StartsASentence(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var character = text[i];

            if (character == '\n')
                return true;

            if (char.IsWhiteSpace(character))
                continue;

            return character is '.' or '!' or '?' or ':';
        }

        return true;
    }

    /// <summary>The singular form of a plural auxiliary, keeping the apostrophe the model used.</summary>
    private static string Singular(string verb)
    {
        var apostrophe = verb.Contains('’') ? "’" : "'";
        return verb.ToLowerInvariant().Replace('’', '\'') switch
        {
            "do" => "does",
            "have" => "has",
            "are" => "is",
            "were" => "was",
            "don't" => $"doesn{apostrophe}t",
            "haven't" => $"hasn{apostrophe}t",
            "aren't" => $"isn{apostrophe}t",
            "weren't" => $"wasn{apostrophe}t",
            _ => verb,
        };
    }

    private static string Capitalise(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>
    /// Tolerates case and a separator between the token's parts, like
    /// <see cref="NamePlaceholder"/>'s pattern and for the same reason — these are the shapes a
    /// small model actually returns once asked to write a token into prose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A trailing apostrophe-s is swallowed rather than left behind: a model that writes
    /// "CardiTrackCardiMemberTheir's walk" has reached for the possessive twice, and "his's walk"
    /// is a worse thing to show a caregiver than the sentence it meant.
    /// </para>
    /// <para>
    /// Any other apostrophe after the token refuses the match outright, which is the difference
    /// between a caught failure and a silent one. A model writing the natural contraction
    /// "CardiTrackCardiMemberThey're" would otherwise have "They'" consumed and replaced, leaving
    /// "here" — a real word, with no token left for <see cref="IsPresentIn"/> to catch and nothing
    /// to stop it being stored. Left unmatched, the token survives resolution and the copy is
    /// discarded by the caller that checks for one.
    /// </para>
    /// <para>
    /// Whitespace is the one separator not tolerated between the name half and the suffix, though
    /// <see cref="NamePlaceholder"/>'s pattern tolerates it inside the name itself. "CardiTrackCardiMember
    /// their" with a space is far more likely to be the model writing the name token and then an
    /// ordinary word than a mangled pronoun token, and swallowing the name in that case would
    /// cost a caregiver the one word this whole mechanism exists to produce.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"CardiTrack[\s_-]*Cardi[\s_-]*Member[_-]*(?<form>They|Them|Their)(?:['’]s)?\b(?!['’])",
        RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();

    /// <summary>
    /// The subject token, then at most one adverb ("typically", "usually", "also", "still"), then a
    /// plural auxiliary. Captured as <c>lead</c> (token and gap, kept as written) and <c>verb</c>.
    /// </summary>
    [GeneratedRegex(
        @"(?<lead>CardiTrack[\s_-]*Cardi[\s_-]*Member[_-]*They\b(?!['’])\s+(?:(?:\w+ly|also|still|often|never|always|just)\s+)?)(?<verb>don['’]t|haven['’]t|aren['’]t|weren['’]t|do|have|are|were)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SubjectVerbPattern();

    /// <summary>
    /// What a leftover looks like: the token in any shape at all, whatever follows it. See
    /// <see cref="IsPresentIn"/> for why this is the looser of the two.
    /// </summary>
    [GeneratedRegex(
        @"CardiTrack[\s_-]*Cardi[\s_-]*Member[_-]*(?:They|Them|Their)",
        RegexOptions.IgnoreCase)]
    private static partial Regex TokenPresencePattern();
}
