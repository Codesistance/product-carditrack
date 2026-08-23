using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The selection policy every advise reader shares: the question's named topic first, then the
/// general row, then the most recent — servable rows only at every step, so no fallback ever
/// serves what the details card would withhold.
/// </summary>
public class AdvisePickerTests
{
    private static MemberAdvise Row(AdviseTopic topic, double ageHours = 6, string? cited = "WHO guidance") => new()
    {
        CardiMemberId = Guid.NewGuid(),
        Topic = topic,
        Summary = $"{topic} summary.",
        Suggestion = $"{topic} suggestion.",
        GuidelineCited = cited,
        GeneratedAtUtc = DateTime.UtcNow.AddHours(-ageHours),
    };

    [Theory]
    [InlineData("does he need help with his sleep?", AdviseTopic.Sleep)]
    [InlineData("how do we get her sleeping better", AdviseTopic.Sleep)]
    [InlineData("what can I do about how little he's walking?", AdviseTopic.Activity)]
    [InlineData("should she be more active?", AdviseTopic.Activity)]
    [InlineData("anything to help his heart rate?", AdviseTopic.HeartRate)]
    public void TopicOf_ReadsTheQuestionsOwnWords(string question, AdviseTopic expected) =>
        Assert.Equal(expected, AdvisePicker.TopicOf(question));

    [Theory]
    [InlineData("should I be worried about him?")]
    [InlineData("what could we try?")]
    public void TopicOf_NamesNothing_WhenTheQuestionNamesNothing(string question) =>
        Assert.Null(AdvisePicker.TopicOf(question));

    [Fact]
    public void Pick_ServesTheNamedTopicsRow()
    {
        var sleep = Row(AdviseTopic.Sleep);
        var rows = new[] { Row(AdviseTopic.Activity), sleep, Row(AdviseTopic.General) };

        Assert.Same(sleep, AdvisePicker.Pick("does he need help with his sleep?", rows, DateTime.UtcNow));
    }

    [Fact]
    public void Pick_FallsBackToGeneral_WhenTheNamedTopicHasNoRow()
    {
        var general = Row(AdviseTopic.General);
        var rows = new[] { Row(AdviseTopic.Activity), general };

        Assert.Same(general, AdvisePicker.Pick("does he need help with his sleep?", rows, DateTime.UtcNow));
    }

    [Fact]
    public void Pick_FallsBackToTheMostRecent_WhenThereIsNoGeneralRowEither()
    {
        var newer = Row(AdviseTopic.Activity, ageHours: 2);
        var rows = new[] { Row(AdviseTopic.HeartRate, ageHours: 20), newer };

        Assert.Same(newer, AdvisePicker.Pick("does he need help with his sleep?", rows, DateTime.UtcNow));
    }

    /// <summary>An exact topic match that is not servable never wins over a servable fallback —
    /// the named row's staleness is not the caregiver's problem to inherit.</summary>
    [Fact]
    public void Pick_NeverServesAnUnservableRow_EvenOnAnExactTopicMatch()
    {
        var staleSleep = Row(AdviseTopic.Sleep, ageHours: 24 * 30);
        var general = Row(AdviseTopic.General);

        var picked = AdvisePicker.Pick("does he need help with his sleep?", [staleSleep, general], DateTime.UtcNow);

        Assert.Same(general, picked);
    }

    [Fact]
    public void Pick_ReturnsNull_WhenNothingIsServable()
    {
        var rows = new[] { Row(AdviseTopic.Sleep, cited: null), Row(AdviseTopic.General, ageHours: 24 * 30) };

        Assert.Null(AdvisePicker.Pick("anything to help?", rows, DateTime.UtcNow));
        Assert.Null(AdvisePicker.PickDefault(rows, DateTime.UtcNow));
    }

    [Fact]
    public void PickDefault_PrefersGeneral_ThenMostRecent()
    {
        var general = Row(AdviseTopic.General, ageHours: 20);
        var newerSleep = Row(AdviseTopic.Sleep, ageHours: 1);

        Assert.Same(general, AdvisePicker.PickDefault([newerSleep, general], DateTime.UtcNow));
        Assert.Same(newerSleep, AdvisePicker.PickDefault([newerSleep, Row(AdviseTopic.HeartRate, ageHours: 9)], DateTime.UtcNow));
    }
}
