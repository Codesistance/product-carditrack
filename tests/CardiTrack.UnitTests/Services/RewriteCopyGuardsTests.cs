using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The backstop for the half of a two-slot generation a caregiver actually reads. Both guards were
/// written from one failure seen in development: a summary that told a family about an oxygen
/// reading the clinical read never mentioned, under a rewrite that had picked the member's sex for
/// itself.
/// </summary>
/// <remarks>
/// The fixtures below have the shape of that read and that summary and none of its numbers.
/// Committed test data is a durable, public record, and a real member's readings are health data
/// whether or not a name travels with them — so the figures are invented, and every one of them
/// could be: what these tests turn on is which readings each text names, never what any of them
/// measured.
/// </remarks>
public class RewriteCopyGuardsTests
{
    private const string TheCardsRead =
        "finding: The patient's recent readings show an increase in heart rate compared to their "
        + "usual resting rate, reaching up to 111 bpm yesterday evening. Their overnight heart rate "
        + "variability was also significantly lower than usual. Sleep duration was well off the "
        + "usual amount. Activity levels were very low yesterday, with only 1,000 steps taken. "
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
    [InlineData("Steps were low.", "finding: Activity levels were very low, with only 1,000 steps taken.")]
    [InlineData("A short walk would help.", "finding: Steps sit below their own baseline. what would help: a walk.")]
    [InlineData("Their heart rate ran higher.", "finding: heart rate reached 111 bpm.")]
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
    /// The reflexives are their own words, so the boundary that keeps "his" out of "history" also
    /// keeps "him" out of "himself". A sentence whose only sexed word is the reflexive states a
    /// sex as plainly as one that says "he".
    /// </summary>
    [Theory]
    [InlineData(Gender.PreferNotToSay, "CardiTrackCardiMember made the tea himself.", true)]
    [InlineData(Gender.PreferNotToSay, "CardiTrackCardiMember made the tea herself.", true)]
    [InlineData(Gender.Male, "CardiTrackCardiMember made the tea herself.", true)]
    [InlineData(Gender.Female, "CardiTrackCardiMember made the tea himself.", true)]
    [InlineData(Gender.Male, "CardiTrackCardiMember made the tea himself.", false)]
    [InlineData(Gender.Female, "CardiTrackCardiMember made the tea herself.", false)]
    public void A_reflexive_states_a_sex_too(Gender gender, string copy, bool expected) =>
        Assert.Equal(expected, RewriteCopyGuards.StatesAnUnsupportedSex(copy, gender));

    /// <summary>
    /// "Heart rate variability" contains "heart rate", so a read that measured only the
    /// variability used to vouch for a summary claiming the rate itself — a different reading,
    /// and one nobody had taken.
    /// </summary>
    [Fact]
    public void A_read_about_variability_does_not_vouch_for_the_heart_rate() =>
        Assert.Equal(
            "heart rate",
            RewriteCopyGuards.NamesAReadingTheReadDidNot(
                "CardiTrackCardiMemberTheir heart rate was higher than usual.",
                "finding: heart rate variability was lower than usual overnight."));

    /// <summary>And the pair the other way round, which must still pass.</summary>
    [Fact]
    public void A_read_about_the_heart_rate_vouches_for_the_heart_rate() =>
        Assert.Null(RewriteCopyGuards.NamesAReadingTheReadDidNot(
            "CardiTrackCardiMemberTheir heart rate was higher than usual.",
            "finding: heart rate ran above this member's usual resting rate, and heart rate "
            + "variability was lower overnight."));

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
