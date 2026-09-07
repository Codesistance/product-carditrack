using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The selection policy every advise reader shares: the named topic first, then the general
/// row, then the most recent — servable rows only at every step, so no fallback ever serves
/// what the details card would withhold. The topic is the routing call's, not a keyword list.
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

    [Fact]
    public void Pick_ServesTheNamedTopicsRow()
    {
        var sleep = Row(AdviseTopic.Sleep);
        var rows = new[] { Row(AdviseTopic.Activity), sleep, Row(AdviseTopic.General) };

        Assert.Same(sleep, AdvisePicker.Pick(AdviseTopic.Sleep, rows, DateTime.UtcNow));
    }

    [Fact]
    public void Pick_FallsBackToGeneral_WhenTheNamedTopicHasNoRow()
    {
        var general = Row(AdviseTopic.General);
        var rows = new[] { Row(AdviseTopic.Activity), general };

        Assert.Same(general, AdvisePicker.Pick(AdviseTopic.Sleep, rows, DateTime.UtcNow));
    }

    [Fact]
    public void Pick_FallsBackToTheMostRecent_WhenThereIsNoGeneralRowEither()
    {
        var newer = Row(AdviseTopic.Activity, ageHours: 2);
        var rows = new[] { Row(AdviseTopic.HeartRate, ageHours: 20), newer };

        Assert.Same(newer, AdvisePicker.Pick(AdviseTopic.Sleep, rows, DateTime.UtcNow));
    }

    /// <summary>An exact topic match that is not servable never wins over a servable fallback —
    /// the named row's staleness is not the caregiver's problem to inherit.</summary>
    [Fact]
    public void Pick_NeverServesAnUnservableRow_EvenOnAnExactTopicMatch()
    {
        var staleSleep = Row(AdviseTopic.Sleep, ageHours: 24 * 30);
        var general = Row(AdviseTopic.General);

        var picked = AdvisePicker.Pick(AdviseTopic.Sleep, [staleSleep, general], DateTime.UtcNow);

        Assert.Same(general, picked);
    }

    [Fact]
    public void Pick_ReturnsNull_WhenNothingIsServable()
    {
        var rows = new[] { Row(AdviseTopic.Sleep, cited: null), Row(AdviseTopic.General, ageHours: 24 * 30) };

        Assert.Null(AdvisePicker.Pick(null, rows, DateTime.UtcNow));
        Assert.Null(AdvisePicker.PickDefault(rows, DateTime.UtcNow));
    }

    /// <summary>A question the router did not pin to a topic still gets a suggestion — the
    /// same fallback a details-card reader with no question uses.</summary>
    [Fact]
    public void Pick_WithNoTopic_UsesTheDefaultFallback()
    {
        var general = Row(AdviseTopic.General);
        var sleep = Row(AdviseTopic.Sleep, ageHours: 1);

        Assert.Same(general, AdvisePicker.Pick(null, [sleep, general], DateTime.UtcNow));
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
