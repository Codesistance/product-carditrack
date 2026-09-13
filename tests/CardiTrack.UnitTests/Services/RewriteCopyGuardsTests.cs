using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The backstop for the half of a two-slot generation a caregiver actually reads. Both guards are
/// written against one real card (2026-09-11): a summary that told a family about an oxygen
/// reading the clinical read never mentioned, under a rewrite that had picked the member's sex for
/// itself.
/// </summary>
public class RewriteCopyGuardsTests
{
    private const string TheCardsRead =
        "finding: The patient's recent readings show an increase in heart rate compared to their "
        + "usual resting rate, reaching up to 119 bpm yesterday evening. Their overnight heart rate "
        + "variability was also significantly lower than usual. Sleep duration was well off the "
        + "usual amount. Activity levels were very low yesterday, with only 2,419 steps taken. "
        + "Breathing rate while asleep was slightly higher than usual. The longest stretch of "
        + "stillness observed was relatively short.";

    private const string TheCardsSummary =
        "CardiTrackCardiMember spent a lot of time resting today, with fewer steps and a long "
        + "period of sitting still. While CardiTrackCardiMemberTheir breathing and oxygen levels "
        + "remained stable, CardiTrackCardiMemberTheir heart rate ran slightly higher than usual.";

    [Fact]
    public void The_card_that_prompted_this_is_caught_for_the_reading_it_invented() =>
        Assert.Equal("oxygen", RewriteCopyGuards.NamesAReadingTheReadDidNot(TheCardsSummary, TheCardsRead));

    /// <summary>
    /// The limit, stated as a test so it cannot be mistaken for an oversight. The same card turned
    /// "the longest stretch of stillness was relatively short" into "a long period of sitting
    /// still" — a flat contradiction of a reading the read did name, which this guard does not and
    /// cannot catch. Deleting the oxygen sentence leaves copy that passes.
    /// </summary>
    [Fact]
    public void A_contradiction_of_a_reading_the_read_did_name_is_not_caught() =>
        Assert.Null(RewriteCopyGuards.NamesAReadingTheReadDidNot(
            "CardiTrackCardiMember spent a lot of time resting today, with fewer steps and a long "
            + "period of sitting still.",
            TheCardsRead));

    [Theory]
    [InlineData("Sleep was shorter than usual.", "finding: Sleep duration was well off the usual amount.")]
    [InlineData("Steps were low.", "finding: Activity levels were very low, with only 2,419 steps taken.")]
    [InlineData("A short walk would help.", "finding: Steps sit below their own baseline. what would help: a walk.")]
    [InlineData("Their heart rate ran higher.", "finding: heart rate reached 119 bpm.")]
    [InlineData("A quiet day all round.", "finding: Activity levels were very low.")]
    public void Copy_that_stays_within_the_read_passes(string copy, string read) =>
        Assert.Null(RewriteCopyGuards.NamesAReadingTheReadDidNot(copy, read));

    /// <summary>
    /// "Under the weather" is about a person, not a reading, and it was in the very card this
    /// guard was written from — a weather family would have rejected sound copy over an idiom.
    /// </summary>
    [Fact]
    public void An_everyday_idiom_is_not_a_reading() =>
        Assert.Null(RewriteCopyGuards.NamesAReadingTheReadDidNot(
            "CardiTrackCardiMemberThey might be feeling a bit under the weather.",
            "finding: heart rate was higher than usual on a quiet day."));

    /// <summary>
    /// "Still" is an adverb before it is a reading. A read about sleep and a summary saying
    /// someone is still resting must not be a rejection.
    /// </summary>
    [Fact]
    public void The_adverb_still_is_not_the_stillness_reading() =>
        Assert.Null(RewriteCopyGuards.NamesAReadingTheReadDidNot(
            "CardiTrackCardiMemberThey is still sleeping more than usual.",
            "finding: Sleep duration was well above the usual amount."));

    [Theory]
    [InlineData(Gender.PreferNotToSay, "His sleep was short.", true)]
    [InlineData(Gender.PreferNotToSay, "Her sleep was short.", true)]
    [InlineData(Gender.Male, "Her sleep was short.", true)]
    [InlineData(Gender.Female, "He slept badly.", true)]
    [InlineData(Gender.Male, "His sleep was short.", false)]
    [InlineData(Gender.Female, "Her sleep was short.", false)]
    [InlineData(Gender.PreferNotToSay, "CardiTrackCardiMemberTheir sleep was short.", false)]
    [InlineData(Gender.PreferNotToSay, "Their sleep was short.", false)]
    [InlineData(Gender.PreferNotToSay, "", false)]
    public void A_sex_is_only_allowed_when_the_record_bears_it_out(
        Gender gender, string copy, bool expected) =>
        Assert.Equal(expected, RewriteCopyGuards.StatesAnUnsupportedSex(copy, gender));

    /// <summary>
    /// Word boundaries, not substrings: "history" is not "his", and "there" is not "her". A guard
    /// that fired on those would discard a summary a week.
    /// </summary>
    [Theory]
    [InlineData("There is a history of quiet afternoons.")]
    [InlineData("The shed door was open.")]
    public void Ordinary_words_containing_a_pronoun_are_not_a_stated_sex(string copy) =>
        Assert.False(RewriteCopyGuards.StatesAnUnsupportedSex(copy, Gender.PreferNotToSay));
}
