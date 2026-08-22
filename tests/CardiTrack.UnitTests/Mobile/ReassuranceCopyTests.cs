using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Alerts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The sentence a family reads about their relative being fine. Worth pinning: it is the one
/// message in the app whose job is to be believed, and the two ways it can go wrong — over-
/// promising, or implying an episode that never happened — are both wording, not logic.
/// </summary>
public class ReassuranceCopyTests
{
    private static ReassuranceResponse Reassurance(int days, bool followsAnAlert = true) => new()
    {
        QuietDays = days,
        QuietSince = DateTime.UtcNow.AddDays(-days),
        FollowsAnAlert = followsAnAlert,
    };

    [Fact]
    public void NamesThePerson_AndSaysTheWatchIsStillRunning()
    {
        var detail = ReassuranceCopy.Detail(Reassurance(9), "Margaret");

        Assert.Contains("Margaret", detail);
        Assert.Contains("9 days", detail);
        // The half that makes it reassurance rather than an empty state.
        Assert.Contains("still watching", detail);
    }

    [Fact]
    public void SaysSinceWeStartedWatching_WhenThereWasNeverAnAlert()
    {
        var detail = ReassuranceCopy.Detail(Reassurance(45, followsAnAlert: false), "Margaret");

        // "Nothing for 45 days" about a member who never alerted invites the reading that one is
        // now overdue.
        Assert.Contains("we've been watching", detail);
    }

    [Theory]
    [InlineData(7, "7 days")]
    [InlineData(13, "13 days")]
    [InlineData(14, "2 weeks")]
    [InlineData(34, "4 weeks")]
    public void ScalesTheUnit_SoLongStretchesStayReadable(int quietDays, string expected)
    {
        Assert.Contains(expected, ReassuranceCopy.Detail(Reassurance(quietDays), "Margaret"));
    }

    [Fact]
    public void FallsBackToThem_RatherThanADatabaseNoun_WhenTheNameIsMissing()
    {
        var detail = ReassuranceCopy.Detail(Reassurance(9), firstName: null);

        Assert.Contains("them", detail);
        Assert.DoesNotContain("CardiMember", detail);
    }

    [Fact]
    public void NeverPromisesMoreThanMonitoringCanKeep()
    {
        var detail = ReassuranceCopy.Detail(Reassurance(30), "Margaret");

        // A caregiver who reads "everything is fine" and is then called about a fall stops
        // believing the rest of the screen. The claim stays narrow: nothing has come up.
        Assert.Contains("Nothing has come up", detail);
        Assert.DoesNotContain("fine", detail);
        Assert.DoesNotContain("healthy", detail);
        Assert.DoesNotContain("perfect", detail);
    }
}
