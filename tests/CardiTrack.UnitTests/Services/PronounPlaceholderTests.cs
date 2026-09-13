using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The pronoun a family reads is the member's, looked up, not the model's, guessed. These pin the
/// three ways that can go wrong: the wrong word for the sex on file, the right word in the wrong
/// case, and a token left standing where neither a sex nor a name could settle it.
/// </summary>
public class PronounPlaceholderTests
{
    [Theory]
    [InlineData(Gender.Male, "Dad had a quiet day, and his sleep was short.")]
    [InlineData(Gender.Female, "Mum had a quiet day, and her sleep was short.")]
    public void Resolve_UsesTheSexOnFile(Gender gender, string expected)
    {
        var name = gender == Gender.Male ? "Dad" : "Mum";
        var generated = $"{NamePlaceholder.Token} had a quiet day, and {PronounPlaceholder.Possessive} sleep was short.";

        Assert.Equal(expected, NamePlaceholder.Resolve(
            PronounPlaceholder.Resolve(generated, gender, name), name));
    }

    /// <summary>
    /// The case <c>MedicalPromptBlocks.Pronouns</c>'s remark settled in prose and could only ask
    /// for: with no sex on file the name is repeated rather than a "they" — a stranger's word for
    /// a family reading about one specific person — and rather than a coin toss between he and
    /// she, which is what the briefs were doing instead.
    /// </summary>
    [Fact]
    public void Resolve_RepeatsTheName_WhenSexIsNotStated() =>
        Assert.Equal(
            "Dad rested. Dad's steps were low, so sit with Dad.",
            new MemberVoice(Gender.PreferNotToSay, "Dad").Resolve(
                $"{NamePlaceholder.Token} rested. {PronounPlaceholder.Possessive} steps were low, "
                + $"so sit with {PronounPlaceholder.Object}."));

    [Theory]
    [InlineData("{0} rested well.", "He rested well.")]
    [InlineData("Today {0} rested well.", "Today he rested well.")]
    [InlineData("A quiet day. {0} rested well.", "A quiet day. He rested well.")]
    [InlineData("A quiet day, and {0} rested well.", "A quiet day, and he rested well.")]
    [InlineData("Worth noting:\n{0} rested well.", "Worth noting:\nHe rested well.")]
    public void Resolve_CapitalisesOnlyWhereASentenceStarts(string template, string expected) =>
        Assert.Equal(
            expected,
            PronounPlaceholder.Resolve(
                string.Format(template, PronounPlaceholder.Subject), Gender.Male, "Dad"));

    /// <summary>
    /// A model that reaches for the possessive twice writes "CardiTrackCardiMemberTheir's". The
    /// apostrophe is swallowed rather than left to become "his's".
    /// </summary>
    [Theory]
    [InlineData("Their", "His walk")]
    [InlineData("Their's", "His walk")]
    [InlineData("their", "His walk")]
    [InlineData("_Their", "His walk")]
    public void Resolve_HandlesTheShapesASmallModelReturns(string suffix, string expected) =>
        Assert.Equal(expected, PronounPlaceholder.Resolve(
            $"{NamePlaceholder.Token}{suffix} walk", Gender.Male, "Dad"));

    /// <summary>
    /// A contraction on the token is refused outright rather than half-resolved. "They’re" would
    /// otherwise have "They’" consumed and replaced, leaving "here" — a real word, with no token
    /// left for <see cref="PronounPlaceholder.IsPresentIn"/> to catch. Unmatched, the token
    /// survives and the caller discards the copy.
    /// </summary>
    [Theory]
    [InlineData("They're resting well.")]
    [InlineData("They’ve been quieter.")]
    [InlineData("Them'll do.")]
    public void Resolve_RefusesAContractionOnTheToken(string tail)
    {
        var generated = $"{NamePlaceholder.Token}{tail}";

        var resolved = new MemberVoice(Gender.Male, "Dad").Resolve(generated);

        Assert.Equal(generated, resolved);
        Assert.True(MemberVoice.IsUnresolvedIn(resolved));
    }

    /// <summary>
    /// The name pattern's lookahead admits the same separators the pronoun pattern does, so a
    /// name-only caller cannot turn "CardiTrackCardiMember_Their" into "Dad_Their". It used to,
    /// and only the order <see cref="MemberVoice"/> resolves in hid it.
    /// </summary>
    [Theory]
    [InlineData("Their")]
    [InlineData("_Their")]
    [InlineData("-Them")]
    [InlineData("__They")]
    public void TheNamePattern_LeavesEverySeparatorFormOfAPronounTokenAlone(string suffix)
    {
        var generated = $"{NamePlaceholder.Token}{suffix} walk";

        Assert.Equal(generated, NamePlaceholder.Resolve(generated, "Dad"));
    }

    /// <summary>
    /// Unresolvable is left standing, like <see cref="NamePlaceholder.Resolve"/> does with a name
    /// it cannot substitute: the caller has to be able to tell, so it can discard the copy rather
    /// than store a sentence with a hole in it.
    /// </summary>
    [Fact]
    public void Resolve_LeavesTheTokenStanding_WhenNeitherSexNorNameIsKnown()
    {
        var generated = $"{PronounPlaceholder.Subject} rested.";

        Assert.Equal(generated, PronounPlaceholder.Resolve(generated, Gender.PreferNotToSay, null));
        Assert.True(PronounPlaceholder.IsPresentIn(
            PronounPlaceholder.Resolve(generated, Gender.PreferNotToSay, null)));
    }

    /// <summary>
    /// The two token families share a prefix, so each pattern has to leave the other's tokens
    /// alone. Resolving the name first is what used to produce "DadTheir"; the name pattern now
    /// refuses the three suffixes outright, which is what makes <see cref="MemberVoice.Resolve"/>
    /// safe rather than merely well-ordered.
    /// </summary>
    [Fact]
    public void TheNamePattern_LeavesPronounTokensAlone()
    {
        var generated = $"{PronounPlaceholder.Possessive} sleep";

        Assert.Equal(generated, NamePlaceholder.Resolve(generated, "Dad"));
        Assert.False(NamePlaceholder.IsPresentIn(generated));
    }

    /// <summary>
    /// And the other direction: a name token followed by an ordinary "their" is far commoner than
    /// a token mangled with a space in it, so the space is not tolerated inside a pronoun token.
    /// </summary>
    [Fact]
    public void ThePronounPattern_LeavesAnOrdinaryWordAfterTheNameAlone() =>
        Assert.Equal(
            "Dad their doctor",
            new MemberVoice(Gender.Male, "Dad").Resolve($"{NamePlaceholder.Token} their doctor"));

    [Theory]
    [InlineData("CardiTrackCardiMemberThey rested.", true)]
    [InlineData("carditrackcardimember_them", true)]
    [InlineData("CardiTrackCardiMember rested.", false)]
    [InlineData("He rested.", false)]
    [InlineData(null, false)]
    public void IsPresentIn_DetectsWhatResolveWouldReplace(string? text, bool expected) =>
        Assert.Equal(expected, PronounPlaceholder.IsPresentIn(text));

    /// <summary>
    /// What every caller actually uses: both halves, in the one order that is safe, and one check
    /// that either was left behind.
    /// </summary>
    [Fact]
    public void MemberVoice_ResolvesBothHalvesAndReportsWhatItCouldNot()
    {
        var voice = new MemberVoice(Gender.Female, "Mum");
        var generated = $"{NamePlaceholder.Token} slept badly, so {PronounPlaceholder.Subject} is tired.";

        var resolved = voice.Resolve(generated);

        Assert.Equal("Mum slept badly, so she is tired.", resolved);
        Assert.False(MemberVoice.IsUnresolvedIn(resolved));
        Assert.True(MemberVoice.IsUnresolvedIn(
            new MemberVoice(Gender.PreferNotToSay, null).Resolve(generated)));
    }
}
