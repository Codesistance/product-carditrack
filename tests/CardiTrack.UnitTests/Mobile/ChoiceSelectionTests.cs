using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The rule the app's own dropdown follows, pinned to the platform Picker's so a page written
/// against one behaves the same against the other. The clause worth the most is the last: a
/// change is announced only when the index actually moved, because the alarm builder rebuilds its
/// lists on every edit and would otherwise refresh itself in a loop.
/// </summary>
public class ChoiceSelectionTests
{
    private static ChoiceSelection Three()
    {
        var selection = new ChoiceSelection();
        selection.SetOptions(["one", "two", "three"]);
        return selection;
    }

    [Fact]
    public void StartsWithNothingChosen()
    {
        var selection = new ChoiceSelection();

        Assert.Equal(-1, selection.SelectedIndex);
        Assert.Null(selection.SelectedItem);
        Assert.Empty(selection.Options);
    }

    [Fact]
    public void SelectingARow_MovesAndReportsIt()
    {
        var selection = Three();

        Assert.True(selection.Select(1));
        Assert.Equal(1, selection.SelectedIndex);
        Assert.Equal("two", selection.SelectedItem);
    }

    [Fact]
    public void SelectingTheSameRowAgain_IsNotAChange()
    {
        var selection = Three();
        selection.Select(1);

        Assert.False(selection.Select(1));
    }

    [Fact]
    public void ARequestPastTheEnd_SettlesOnTheLastRow()
    {
        var selection = Three();

        Assert.True(selection.Select(7));
        Assert.Equal(2, selection.SelectedIndex);
    }

    [Fact]
    public void ARequestBelowNone_SettlesOnNone()
    {
        var selection = Three();
        selection.Select(1);

        Assert.True(selection.Select(-5));
        Assert.Equal(-1, selection.SelectedIndex);
        Assert.Null(selection.SelectedItem);
    }

    [Fact]
    public void SettlingWhereItAlreadyIs_IsNotAChange()
    {
        var selection = Three();
        selection.Select(2);

        // Past the end again, which settles on the same last row it is already on.
        Assert.False(selection.Select(9));
    }

    [Fact]
    public void AnEmptyList_HoldsNothingChosen()
    {
        var selection = new ChoiceSelection();

        Assert.False(selection.Select(0));
        Assert.Equal(-1, selection.SelectedIndex);
    }

    [Fact]
    public void SwappingTheOptions_ClearsTheSelection_AndSaysSo()
    {
        var selection = Three();
        selection.Select(1);

        Assert.True(selection.SetOptions(["a", "b"]));
        Assert.Equal(-1, selection.SelectedIndex);
        Assert.Equal(["a", "b"], selection.Options);
    }

    [Fact]
    public void SwappingTheOptions_WithNothingChosen_IsNotAChange()
    {
        var selection = Three();

        Assert.False(selection.SetOptions(["a", "b"]));
    }

    [Fact]
    public void ANullList_IsAnEmptyOne()
    {
        var selection = Three();
        selection.Select(0);

        Assert.True(selection.SetOptions(null));
        Assert.Empty(selection.Options);
        Assert.Equal(-1, selection.SelectedIndex);
    }

    [Fact]
    public void SelectingByLabel_FindsTheRow()
    {
        var selection = Three();

        Assert.True(selection.Select("three"));
        Assert.Equal(2, selection.SelectedIndex);
    }

    [Fact]
    public void SelectingAnUnknownLabel_SelectsNone()
    {
        var selection = Three();
        selection.Select(1);

        Assert.True(selection.Select("four"));
        Assert.Equal(-1, selection.SelectedIndex);
    }

    [Theory]
    [InlineData(0, 3, 0)]
    [InlineData(2, 3, 2)]
    [InlineData(3, 3, 2)]
    [InlineData(-1, 3, -1)]
    [InlineData(-4, 3, -1)]
    [InlineData(0, 0, -1)]
    [InlineData(-1, 0, -1)]
    public void Clamp_HoldsTheIndexInsideTheList(int requested, int count, int expected)
    {
        Assert.Equal(expected, ChoiceSelection.Clamp(requested, count));
    }
}
