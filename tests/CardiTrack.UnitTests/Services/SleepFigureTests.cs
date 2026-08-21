using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Sleep is stored in minutes because that is what the wearable reports, and it was being sent to
/// the models that way — so a caregiver asking how their father slept was told "372 minutes",
/// arithmetic homework in the middle of a sentence meant to reassure. This is the one place that
/// number becomes something a person would say.
/// </summary>
public class SleepFigureTests
{
    [Theory]
    [InlineData(372, "6h 12m")]
    [InlineData(480, "8h")]
    [InlineData(61, "1h 1m")]
    [InlineData(59, "59m")]
    [InlineData(0, "0m")]
    public void SleepFigure_ReadsTheWayANightIsSpokenAbout(int minutes, string expected) =>
        Assert.Equal(expected, MedicalPromptBlocks.SleepFigure(minutes));

    /// <summary>
    /// A whole number of hours drops the minutes rather than writing "8h 0m", which reads as a
    /// measurement precise to the minute that happened to land on zero.
    /// </summary>
    [Fact]
    public void AWholeNumberOfHours_DoesNotTrailAZero() =>
        Assert.Equal("7h", MedicalPromptBlocks.SleepFigure(420));

    /// <summary>
    /// Under an hour stays in minutes: "0h 40m" is a worse way of writing forty minutes, and this
    /// figure goes into a prompt where every extra token is paid for in prompt-evaluation time.
    /// </summary>
    [Fact]
    public void UnderAnHour_KeepsMinutesAlone() =>
        Assert.Equal("40m", MedicalPromptBlocks.SleepFigure(40));

    /// <summary>
    /// A night the device never recorded says so. Sending nothing, or a bare "0", would let the
    /// model read an unmeasured night as a sleepless one.
    /// </summary>
    [Fact]
    public void AnUnmeasuredNight_SaysSoRatherThanReadingAsZero() =>
        Assert.Equal("not measured", MedicalPromptBlocks.SleepFigure(null));
}
