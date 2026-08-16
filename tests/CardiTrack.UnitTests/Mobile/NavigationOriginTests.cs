using CardiTrack.Mobile.Core.Navigation;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The single step Shell cannot take on its own: a tab jump that dropped the page a caregiver was
/// reading records it, and the next back press at that tab root spends the record instead of
/// closing the app.
/// </summary>
public class NavigationOriginTests
{
    private const string MemberDetail = "//dashboard/memberdetail?memberId=8f1c";

    [Fact]
    public void With_nothing_recorded_there_is_nothing_to_return_to()
    {
        var origin = new NavigationOrigin();

        Assert.False(origin.HasOrigin);
        Assert.False(origin.TryTake(out var taken));
        Assert.Equal(string.Empty, taken);
    }

    [Fact]
    public void A_recorded_origin_is_returned_once()
    {
        var origin = new NavigationOrigin();
        origin.Remember(MemberDetail);

        Assert.True(origin.HasOrigin);
        Assert.True(origin.TryTake(out var taken));
        Assert.Equal(MemberDetail, taken);

        // One jump earns one return. A second back press at the same tab root exits, rather than
        // bouncing the caregiver back to a page they have already left twice.
        Assert.False(origin.HasOrigin);
        Assert.False(origin.TryTake(out _));
    }

    [Fact]
    public void The_most_recent_jump_is_the_one_back_honours()
    {
        var origin = new NavigationOrigin();
        origin.Remember(MemberDetail);
        origin.Remember("//alerts/alertdetail?alertId=42");

        Assert.True(origin.TryTake(out var taken));
        Assert.Equal("//alerts/alertdetail?alertId=42", taken);
    }

    [Fact]
    public void Clearing_ends_the_journey()
    {
        var origin = new NavigationOrigin();
        origin.Remember(MemberDetail);

        origin.Clear();

        // Removing a member clears it: back must not offer to return to someone deleted.
        Assert.False(origin.HasOrigin);
        Assert.False(origin.TryTake(out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_location_that_names_nowhere_is_not_recorded(string? location)
    {
        var origin = new NavigationOrigin();

        origin.Remember(location);

        // Shell can hand back an empty location; recording it would make back appear to have
        // somewhere to go and then navigate to nothing.
        Assert.False(origin.HasOrigin);
        Assert.False(origin.TryTake(out _));
    }

    [Fact]
    public void A_blank_jump_forgets_the_previous_origin_rather_than_keeping_it_alive()
    {
        var origin = new NavigationOrigin();
        origin.Remember(MemberDetail);

        origin.Remember(null);

        // The slot holds where back goes next. Keeping a stale entry would send a later back press
        // to a page the caregiver left two journeys ago.
        Assert.False(origin.HasOrigin);
    }
}
