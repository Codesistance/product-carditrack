using System.Text.RegularExpressions;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The checks every piece of Rewrite-slot copy is held to before it is stored — the code half of
/// two boundaries the rewrite briefs state and cannot enforce: say nothing about the person you
/// were not told, and name no reading the clinical read did not.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the digest and Advise rather than living in either, because the two briefs are the
/// same shape — a clinical read in, family-facing copy out, on a slot that is shown no member
/// context — and a guard on one of them that the other lacks is a gap nobody meant to leave.
/// <see cref="AdviseRegisterGuards"/> and <see cref="JournalRegisterGuards"/> stay where they are:
/// those guard a register, which differs per surface, while these two guard what the copy claims,
/// which does not.
/// </para>
/// <para>
/// Both take the same stance the guards around them take — a prompt is a request, not a guarantee
/// — and both are deliberately absent from the prompts themselves, because a negative list in a
/// brief is a list a small model echoes.
/// </para>
/// </remarks>
internal static partial class RewriteCopyGuards
{
    /// <summary>
    /// True when the copy states a sex the member's record does not bear out — the wrong one, or
    /// any one at all for a member whose sex is not on file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run against the model's reply, before <see cref="MemberVoice.Resolve"/> — afterwards "his"
    /// is exactly what the copy is supposed to say, put there by code that looked it up.
    /// </para>
    /// <para>
    /// Not "any sexed pronoun at all", though the Rewrite slot is told none and so invents every
    /// one it writes. A guess that matches the record is a sentence a family can read without
    /// being told anything untrue, and rejecting it would cost that member their summary for the
    /// day over a word that was right. What is rejected is the guess that is wrong, and the guess
    /// that nothing can confirm — <c>PreferNotToSay</c>, where every member created before M1-04
    /// asked for sex still sits, and where a coin toss about someone's father or mother would
    /// otherwise reach the family as fact.
    /// </para>
    /// <para>
    /// "They", "them" and "their" are deliberately not here. They state nothing untrue about the
    /// person, they are what <see cref="MedicalPromptBlocks.PronounsByToken"/> now asks to be
    /// written as tokens, and they are also the ordinary words for the family the copy is
    /// addressed to — so rejecting them would throw away sound copy over a register slip, and
    /// sometimes over a correct sentence.
    /// </para>
    /// </remarks>
    internal static bool StatesAnUnsupportedSex(string? text, Gender gender)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var male = MalePronouns().IsMatch(text);
        var female = FemalePronouns().IsMatch(text);

        if (!male && !female)
            return false;

        return gender switch
        {
            Gender.Male => female,
            Gender.Female => male,
            _ => true,
        };
    }

    /// <summary>
    /// The first reading the copy names that the clinical read did not, or null when the copy
    /// stays within what it was given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this answers, from a summary card on 2026-09-11: a clinical read that said
    /// breathing was slightly higher than usual and never mentioned oxygen at all came back as
    /// "his breathing and oxygen levels remained stable". Oxygen was not softened or misread — it
    /// was not there. A family reading that card was told a reading had been taken and was fine.
    /// </para>
    /// <para>
    /// Only families named in <see cref="ReadingFamilies"/> can be caught, and only the invention
    /// of one: copy that contradicts a reading the read did name — "relatively short" written up
    /// as "a long period of sitting still", from the same card — matches the same family and
    /// passes here. That is a real limit, not an oversight. A polarity check would need to know
    /// which way each reading was moving and which way the sentence about it points, which is the
    /// clinical read's own job, and guessing at it in a regex would discard sound copy far more
    /// often than it caught a flip.
    /// </para>
    /// <para>
    /// Summaries only, never suggestions. A suggestion is an action, and the brief lets it reach
    /// for an already-known routine fact; an afternoon walk proposed against a read about sleep is
    /// the suggestion working as asked, not an invented reading. The summary is the half that
    /// claims what was measured.
    /// </para>
    /// </remarks>
    internal static string? NamesAReadingTheReadDidNot(string? copy, string? read)
    {
        if (string.IsNullOrWhiteSpace(copy) || string.IsNullOrWhiteSpace(read))
            return null;

        foreach (var (family, words) in ReadingFamilies)
        {
            if (Mentions(copy, words) && !Mentions(read, words))
                return family;
        }

        return null;
    }

    /// <summary>
    /// The readings this platform takes, each with the everyday words a caregiver-facing sentence
    /// would use for it. Stems, matched on a word boundary, so "breath" covers "breathing" and
    /// "breaths" without covering "breathe" being absent from the read for grammatical reasons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three omissions are deliberate. There is no weather family: "under the weather" is an
    /// everyday phrase about a person, not a claim about a reading, and it appeared in the very
    /// card that prompted this guard. "Still" on its own is not in the stillness family — it is
    /// one of the commonest adverbs in English ("he is still resting"), and a family named by an
    /// adverb would reject far more sound copy than it caught. And "moved" is not in the activity
    /// family for the same reason: a summary saying someone moved bedrooms last week is using the
    /// family's own answer to read the day, which is exactly what the brief asks for.
    /// </para>
    /// <para>
    /// A reading absent from this list is never flagged. The guard's claim is only that copy
    /// naming one of these has to have been given it — not that it lists everything a summary may
    /// mention.
    /// </para>
    /// </remarks>
    private static readonly (string Family, string[] Words)[] ReadingFamilies =
    [
        ("oxygen", ["oxygen", "spo2", "saturation", "sats"]),
        ("breathing", ["breath", "breathing", "breaths", "respiratory", "respiration"]),
        ("sleep", ["sleep", "sleeping", "slept", "asleep", "overnight", "nap", "naps", "napped"]),
        ("heart rate", ["heart rate", "heartbeat", "heartbeats", "pulse", "bpm", "beats per minute"]),
        ("heart rate variability", ["variability", "hrv"]),
        ("activity", ["step", "steps", "walk", "walks", "walking", "active", "activity",
            "movement", "exercise", "exercising"]),
        ("stillness", ["stillness", "sitting still", "sedentary", "unbroken"]),
    ];

    private static bool Mentions(string text, string[] words) =>
        words.Any(word => Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase));

    /// <summary>
    /// The pronouns that state a male subject. Word-bounded, so "his" does not fire on "history"
    /// and "he" does not fire on "her".
    /// </summary>
    [GeneratedRegex(@"\b(?:he|him|his)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MalePronouns();

    /// <summary>
    /// The pronouns that state a female subject. Shorter by one than its counterpart because "her"
    /// serves as both the object and the possessive.
    /// </summary>
    [GeneratedRegex(@"\b(?:she|her|hers)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FemalePronouns();
}
