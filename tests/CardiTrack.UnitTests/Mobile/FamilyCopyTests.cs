using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Family;

namespace CardiTrack.UnitTests.Mobile;

public class FamilyCopyTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private static FamilyJoinRequestSummary Request(string status, int expiresInDays = 5) =>
        new(Guid.NewGuid(), "The Does", status, Now.AddHours(-2), Now.AddDays(expiresInDays));

    [Fact]
    public void APendingAskSaysWhoItIsWaitingOn_AndHowLongTheyHave()
    {
        var (title, detail) = FamilyCopy.JoinRequestLine(Request("pending"), Now);

        Assert.Equal("Waiting on The Does", title);
        Assert.Contains("5 days", detail);
        Assert.False(FamilyCopy.CanAskAgain(Request("pending")));
        Assert.True(FamilyCopy.IsPending(Request("pending")));
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("expired")]
    public void ADeclinedOrExpiredAskOffersToAskAgain_WithoutRevealingAnythingNew(string status)
    {
        var (title, detail) = FamilyCopy.JoinRequestLine(Request(status), Now);

        Assert.Contains("The Does", title);
        Assert.Contains("ask again", detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(FamilyCopy.CanAskAgain(Request(status)));
        Assert.DoesNotContain("member", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0.5, "less than an hour")]
    [InlineData(1.5, "1 hour")]
    [InlineData(5, "5 hours")]
    [InlineData(23.9, "23 hours")]
    [InlineData(24, "1 day")]
    [InlineData(25, "2 days")]
    [InlineData(24 * 6.5, "7 days")]
    [InlineData(-1, "no time")]
    public void Remaining_RoundsTheWayACaregiverWouldSayIt(double hoursLeft, string expected)
    {
        Assert.Equal(expected, FamilyCopy.Remaining(Now.AddHours(hoursLeft), Now));
    }

    [Fact]
    public void ShareText_CarriesTheDisplayFormOfTheFamilyId_AndNoLink()
    {
        var text = FamilyCopy.ShareFamilyIdText("The Does", "KTR7M2Q9");

        Assert.Contains("KTR7-M2Q9", text);
        Assert.DoesNotContain("http", text);
    }

    [Theory]
    [InlineData(new string[0], "You can't see anyone in this family yet.")]
    [InlineData(new[] { "Margaret" }, "You can see Margaret.")]
    [InlineData(new[] { "Margaret", "Frank" }, "You can see Margaret and Frank.")]
    [InlineData(new[] { "Margaret", "Frank", "Joan" }, "You can see Margaret, Frank and Joan.")]
    public void WatchedLine_ListsTheMembersNaturally(string[] names, string expected)
    {
        Assert.Equal(expected, FamilyCopy.WatchedLine(names));
    }

    [Fact]
    public void InviteStatus_DistinguishesLiveFromFinished()
    {
        var live = new CaregiverInviteResponse { Status = "pending", ExpiresAt = Now.AddDays(6) };
        var spent = new CaregiverInviteResponse { Status = "accepted", ResolvedAt = Now.AddHours(-1) };

        Assert.True(FamilyCopy.IsLive(live));
        Assert.Contains("6 days", FamilyCopy.InviteStatusLine(live, Now));
        Assert.False(FamilyCopy.IsLive(spent));
        Assert.StartsWith("Accepted", FamilyCopy.InviteStatusLine(spent, Now));
    }

    [Fact]
    public void TheFanOutSentenceNeverNamesAnybody()
    {
        Assert.Equal("Escalated — nobody has acknowledged this yet", FamilyCopy.EscalatedNobodyAnswered);
    }
}
