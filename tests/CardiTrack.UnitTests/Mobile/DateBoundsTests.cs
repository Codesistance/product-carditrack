using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The clamping the app's own date field does, pinned to the platform DatePicker's so the date of
/// birth and export range forms behave as they did before the control changed hands — and the
/// two rules that keep the platform control from throwing while a page sets its bounds one at a
/// time.
/// </summary>
public class DateBoundsTests
{
    private static readonly DateTime Earliest = new(2020, 1, 1);
    private static readonly DateTime Latest = new(2020, 12, 31);

    [Fact]
    public void ADateInsideTheBounds_IsKept()
    {
        var chosen = new DateTime(2020, 6, 15);

        Assert.Equal(chosen, DateBounds.Clamp(chosen, Earliest, Latest));
    }

    [Fact]
    public void ADateBeforeTheEarliest_SettlesOnTheEarliest()
    {
        Assert.Equal(Earliest, DateBounds.Clamp(new DateTime(2019, 3, 3), Earliest, Latest));
    }

    [Fact]
    public void ADateAfterTheLatest_SettlesOnTheLatest()
    {
        Assert.Equal(Latest, DateBounds.Clamp(new DateTime(2021, 3, 3), Earliest, Latest));
    }

    [Fact]
    public void NothingStaysNothing()
    {
        Assert.Null(DateBounds.Clamp(null, Earliest, Latest));
    }

    [Fact]
    public void OnlyTheDayIsKept()
    {
        var withTime = new DateTime(2020, 6, 15, 13, 45, 0);

        Assert.Equal(new DateTime(2020, 6, 15), DateBounds.Clamp(withTime, Earliest, Latest));
    }

    [Fact]
    public void TheBoundsAreReadAsDaysToo()
    {
        // A bound carrying a time of day must not push a date on that same day out of range.
        var latestAtNoon = new DateTime(2020, 12, 31, 12, 0, 0);

        Assert.Equal(new DateTime(2020, 12, 31), DateBounds.Clamp(new DateTime(2020, 12, 31), Earliest, latestAtNoon));
    }

    [Fact]
    public void AnEarliestAboveTheLatest_SettlesAValueOnTheLatest()
    {
        var upsideDown = DateBounds.Clamp(new DateTime(2020, 6, 15), Latest, Earliest);

        Assert.Equal(Earliest, upsideDown);
    }

    [Fact]
    public void TheDefaultsAreThePlatformControls()
    {
        Assert.Equal(new DateTime(1900, 1, 1), DateBounds.DefaultMinimum);
        Assert.Equal(new DateTime(2100, 12, 31), DateBounds.DefaultMaximum);
    }

    // ── Normalising the pair ────────────────────────────────────────────────────

    [Fact]
    public void OrderedBounds_AreKeptAsTheyAre()
    {
        Assert.Equal((Earliest, Latest), DateBounds.Normalise(Earliest, Latest));
    }

    [Fact]
    public void AnEarliestAboveTheLatest_CollapsesOntoTheLatest()
    {
        // Latest wins, the same as Clamp already decided and the count stepper does for its own.
        Assert.Equal((Earliest, Earliest), DateBounds.Normalise(Latest, Earliest));
    }

    [Fact]
    public void TheSameDayForBoth_IsARangeOfOneDay()
    {
        Assert.Equal((Earliest, Earliest), DateBounds.Normalise(Earliest, Earliest));
    }

    [Fact]
    public void NormalisedBounds_AreDays()
    {
        var (earliest, latest) = DateBounds.Normalise(
            new DateTime(2020, 1, 1, 9, 0, 0), new DateTime(2020, 12, 31, 23, 0, 0));

        Assert.Equal(Earliest, earliest);
        Assert.Equal(Latest, latest);
    }

    // ── Handing the pair to the platform control ────────────────────────────────

    [Fact]
    public void ANewEarliestPastTheOldLatest_MovesTheLatestFirst()
    {
        // The control holds ..2020 and the page asks for 2031..: setting 2031 into a control
        // whose latest is still 2020 is the throw, so the latest has to move first.
        Assert.True(DateBounds.LatestFirst(new DateTime(2031, 1, 1), currentMaximum: Latest));
    }

    [Fact]
    public void ANewEarliestInsideTheOldRange_MovesTheEarliestFirst()
    {
        Assert.False(DateBounds.LatestFirst(new DateTime(2020, 6, 1), currentMaximum: Latest));
    }

    [Fact]
    public void ANewEarliestOnTheOldLatest_NeedNotReorder()
    {
        Assert.False(DateBounds.LatestFirst(Latest, currentMaximum: Latest));
    }

    /// <summary>
    /// A page that sets the latest below an earliest it set a moment before. Between the two
    /// writes the field's own bounds are upside down; what reaches the platform control must be
    /// consistent with what it already holds at every step. This is the case the first version
    /// got wrong by deciding the order against the control's latest alone: an earliest still
    /// inside the control's range, followed by a latest below it.
    /// </summary>
    [Fact]
    public void TransientUpsideDownBounds_ReachThePlatformControlConsistently()
    {
        // The control currently holds what the first write left it: 2031..2100.
        var controlMinimum = new DateTime(2031, 1, 1);
        var controlMaximum = DateBounds.DefaultMaximum;

        // Second write: latest = 2020, while the field's earliest is still 2031.
        var (earliest, latest) = DateBounds.Normalise(controlMinimum, Latest);
        Assert.Equal((Latest, Latest), (earliest, latest));

        // The earliest goes first, and neither step is refused: the earliest is not above the
        // latest the control holds, and the latest that follows is not below the earliest just set.
        Assert.False(DateBounds.LatestFirst(earliest, controlMaximum));
        Assert.True(earliest <= controlMaximum);
        Assert.True(latest >= earliest);
    }
}
