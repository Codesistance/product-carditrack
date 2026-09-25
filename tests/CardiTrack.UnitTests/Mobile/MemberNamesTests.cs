using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Members;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The app greets and labels a member by their first name only — the stored one, never a guess
/// cut out of the full name, except when the API is old enough not to send it.
/// </summary>
public class MemberNamesTests
{
    [Fact]
    public void UsesTheStoredFirstName_EvenWhenItIsMoreThanOneWord()
    {
        var member = new CardiMemberResponse { FirstName = "Mary Ann", LastName = "Smith", Name = "Mary Ann Smith" };

        Assert.Equal("Mary Ann", member.DisplayFirstName());
    }

    /// <summary>The app and the API ship separately; an older API sends only the full name.</summary>
    [Fact]
    public void FallsBackToTheFirstWordOfTheFullName_FromAnApiThatPredatesTheSplit()
    {
        var member = new CardiMemberDetailResponse { Name = "Margaret Doe" };

        Assert.Equal("Margaret", member.DisplayFirstName());
        Assert.Equal("Doe", member.DisplayLastName());
    }

    [Fact]
    public void DisplayLastName_IsTheStoredOne_WhenTheApiSendsTheParts()
    {
        var member = new CardiMemberDetailResponse { FirstName = "Arthur", LastName = null, Name = "Arthur" };

        Assert.Null(member.DisplayLastName());
    }

    [Fact]
    public void CoversEveryPayloadThatNamesAMember()
    {
        Assert.Equal("Arthur", new DashboardResponse { FirstName = "Arthur", Name = "Arthur Doe" }.DisplayFirstName());
        Assert.Equal("Arthur", new AlertSummaryResponse { CardiMemberFirstName = "Arthur", CardiMemberName = "Arthur Doe" }.MemberFirstName());
        Assert.Equal("Arthur", new AlertDetailResponse { CardiMemberName = "Arthur Doe" }.MemberFirstName());
    }

    [Fact]
    public void NotificationWithNoMember_HasNoName_SoTheCopyKeepsItsOwnWording()
    {
        Assert.Null(new NotificationResponse().MemberFirstName());
        Assert.Null(new NotificationMuteResponse().MemberFirstName());
        Assert.Equal("Arthur", new NotificationMuteResponse { CardiMemberFirstName = "Arthur", CardiMemberName = "Arthur Doe" }.MemberFirstName());
    }

    [Theory]
    [InlineData(null, "First name is required")]
    [InlineData("  ", "First name is required")]
    [InlineData("A", null)]
    [InlineData("Arthur", null)]
    public void FirstNameRule_MatchesTheApi(string? firstName, string? error)
    {
        Assert.Equal(error, MemberNameRules.FirstNameError(firstName));
    }

    [Fact]
    public void NameRules_CapBothPartsAtTheApisLength()
    {
        var tooLong = new string('a', MemberNameRules.MaxLength + 1);

        Assert.Equal("First name cannot exceed 100 characters", MemberNameRules.FirstNameError(tooLong));
        Assert.Equal("Last name cannot exceed 100 characters", MemberNameRules.LastNameError(tooLong));
        Assert.Null(MemberNameRules.LastNameError(null));
        Assert.Null(MemberNameRules.LastNameError(new string('a', MemberNameRules.MaxLength)));
    }

    [Theory]
    [InlineData("Margaret", "Doe", "Margaret Doe")]
    [InlineData("Arthur", null, "Arthur")]
    [InlineData("A", null, "A.")]
    [InlineData(" A ", "  ", "A.")]
    [InlineData("A", "Smith", "A Smith")]
    public void LegacyName_FitsTheOldApisTwoToHundredRule(string first, string? last, string expected)
    {
        Assert.Equal(expected, MemberNameRules.LegacyName(first, last));
    }

    [Fact]
    public void LegacyName_CutsAnOverlongJoinedNameToTheOldLimit()
    {
        var first = new string('a', MemberNameRules.MaxLength);
        var last = new string('b', MemberNameRules.MaxLength);

        var legacy = MemberNameRules.LegacyName(first, last);

        Assert.Equal(MemberNameRules.LegacyMaxLength, legacy.Length);
        Assert.Equal(first, legacy);
    }

    [Fact]
    public void LegacyName_NeverEndsInTheSpaceACutLandsOn()
    {
        var legacy = MemberNameRules.LegacyName(new string('a', 99), "Smith");

        Assert.Equal(new string('a', 99), legacy);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(" Doe ", "Doe")]
    public void LastNameOrNull_SendsNoneRatherThanBlank(string? typed, string? sent)
    {
        Assert.Equal(sent, MemberNameRules.LastNameOrNull(typed));
    }
}
