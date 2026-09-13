using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// How a caregiver's copy refers to one member: the name to put back, and the sex that decides
/// the pronouns. Everything a model returns about that member passes through
/// <see cref="Resolve"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both halves are swapped after the model has answered, and for the same reason — the name and
/// the sex are the member's, not the generation's, and neither is sent to the Rewrite slot. What
/// this type adds over calling the two placeholders in turn is the order. Pronoun tokens are
/// built on <see cref="NamePlaceholder.Token"/>, so resolving the name first would turn
/// "CardiTrackCardiMemberTheir" into "DadTheir"; the name pattern's lookahead makes that safe
/// either way, but a rule that holds because of a lookahead in another file is a rule the next
/// call site can get wrong. Here there is one order, written once.
/// </para>
/// <para>
/// <see cref="IsUnresolvedIn"/> is the other half of that: every caller that refused to store copy
/// carrying a leftover name token has the same reason to refuse one carrying a leftover pronoun
/// token, and a caller that checks only the first would ship "CardiTrackCardiMemberTheir sleep" to
/// a family.
/// </para>
/// </remarks>
internal readonly record struct MemberVoice(Gender Gender, string? FirstName)
{
    /// <summary>
    /// The voice for <paramref name="member"/> — <see cref="Gender.PreferNotToSay"/> and no name
    /// when there is no member, which resolves nothing and so leaves the tokens for
    /// <see cref="IsUnresolvedIn"/> to catch.
    /// </summary>
    internal static MemberVoice For(CardiMember? member) =>
        new(member?.Gender ?? Gender.PreferNotToSay, NamePlaceholder.FirstName(member?.Name));

    /// <summary>
    /// Puts this member's pronouns and name back into <paramref name="text"/>, pronouns first.
    /// </summary>
    internal string? Resolve(string? text) =>
        NamePlaceholder.Resolve(PronounPlaceholder.Resolve(text, Gender, FirstName), FirstName);

    /// <summary>
    /// True when <paramref name="text"/> still carries a name or pronoun token — copy no caller
    /// may store, whatever else is right with it.
    /// </summary>
    internal static bool IsUnresolvedIn(string? text) =>
        NamePlaceholder.IsPresentIn(text) || PronounPlaceholder.IsPresentIn(text);
}
