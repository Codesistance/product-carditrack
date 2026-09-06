using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// When a stored status line may be shown — one rule for the dashboard header and for chat's
/// status rung, so the sentence at the top of the screen and the answer beneath it cannot disagree
/// about whether there is a current line.
/// </summary>
/// <remarks>
/// Lifted out of <c>HealthInsightService</c>, where it was a private constant with a single reader.
/// That is the setup <see cref="AdviseServability"/> exists because of: two surfaces each with
/// their own idea of fresh, which drifted apart once already.
/// </remarks>
public class StatusLineServabilityTests
{
    private static readonly DateTime Now = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

    private static MemberStatusLine Line(TimeSpan age, string message = "Winding down for the night") =>
        new() { Message = message, Headline = "Settling", GeneratedAtUtc = Now - age };

    [Fact]
    public void AFreshLineIsServable() =>
        Assert.True(StatusLineServability.IsServable(Line(TimeSpan.FromHours(2)), Now));

    /// <summary>
    /// The line regenerates on every digest and assess pass, so a day-old row means generation has
    /// stopped for this member — and yesterday's reassurance presented as current says something
    /// false.
    /// </summary>
    [Fact]
    public void ADayOldLineIsNot() =>
        Assert.False(StatusLineServability.IsServable(Line(TimeSpan.FromHours(25)), Now));

    [Fact]
    public void TheCeilingIsTwentyFourHours()
    {
        Assert.True(StatusLineServability.IsServable(Line(StatusLineStaleness.MaxAge), Now));
        Assert.False(StatusLineServability.IsServable(
            Line(StatusLineStaleness.MaxAge + TimeSpan.FromMinutes(1)), Now));
    }

    /// <summary>
    /// Tighter than Advise's, and deliberately: Advise regenerates roughly daily and its ceiling is
    /// a buffer against one missed pass; this one regenerates every pass, so the same buffer would
    /// mean serving three-day-old copy as current.
    /// </summary>
    [Fact]
    public void ItIsTighterThanAdvises() =>
        Assert.True(StatusLineStaleness.MaxAge < AdviseStaleness.MaxAge);

    [Fact]
    public void NoLineIsNotServable() =>
        Assert.False(StatusLineServability.IsServable(null, Now));

    /// <summary>
    /// The generator keeps the previous row rather than storing a blank message, so this should not
    /// exist — but the property is non-null with an empty default, which makes the row
    /// representable, and a serving rule that assumes a row away is what one of these surfaces got
    /// wrong last time.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ALineWithNothingToSayIsNotServable(string message) =>
        Assert.False(StatusLineServability.IsServable(Line(TimeSpan.FromHours(1), message), Now));

    /// <summary>
    /// The headline is documented as droppable and the dashboard has per-tier copy to fall back on,
    /// so its absence must not withhold the sentence.
    /// </summary>
    [Fact]
    public void AMissingHeadlineDoesNotWithholdTheLine()
    {
        var line = Line(TimeSpan.FromHours(1));
        line.Headline = null;

        Assert.True(StatusLineServability.IsServable(line, Now));
    }
}
