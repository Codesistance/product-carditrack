using CardiTrack.Mobile.Core.Navigation;

namespace CardiTrack.UnitTests.Mobile;

public class MemberRouteTests
{
    private static readonly Guid Member = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Other = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    [Fact]
    public void StartsMissing()
    {
        var route = new MemberRoute();

        Assert.True(route.IsMissing);
        Assert.Equal(Guid.Empty, route.Id);
    }

    [Fact]
    public void TakesTheIdShellPasses()
    {
        var route = new MemberRoute();

        Assert.False(route.Accept(Member.ToString()));
        Assert.Equal(Member, route.Id);
        Assert.False(route.IsMissing);
    }

    [Fact]
    public void UnescapesBeforeParsing()
    {
        var route = new MemberRoute();

        route.Accept(Uri.EscapeDataString(Member.ToString()));

        Assert.Equal(Member, route.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void RefusesAnythingThatCannotIdentifyAMember(string? raw)
    {
        var route = new MemberRoute();

        Assert.False(route.Accept(raw));
        Assert.True(route.IsMissing);
    }

    /// <summary>
    /// The ordinary order: Shell sets the query property, then the page appears and loads. There
    /// was no load to miss, so nothing is owed.
    /// </summary>
    [Fact]
    public void IdBeforeLoadOwesNothing()
    {
        var route = new MemberRoute();

        Assert.False(route.Accept(Member.ToString()));
    }

    /// <summary>
    /// The race this type exists for: the page appeared and loaded first, found no id, and the
    /// route landed afterwards. The load is owed again, now that there is something to ask for.
    /// </summary>
    [Fact]
    public void IdAfterALoadWithoutOneOwesAReload()
    {
        var route = new MemberRoute();

        route.LoadedWithoutId();

        Assert.True(route.Accept(Member.ToString()));
        Assert.Equal(Member, route.Id);
    }

    [Fact]
    public void ReloadIsOwedOnceOnly()
    {
        var route = new MemberRoute();
        route.LoadedWithoutId();
        route.Accept(Member.ToString());

        Assert.False(route.Accept(Other.ToString()));
    }

    [Fact]
    public void RepeatOfTheSameIdIsNotAnArrival()
    {
        var route = new MemberRoute();
        route.Accept(Member.ToString());
        route.LoadedWithoutId();

        Assert.False(route.Accept(Member.ToString()));
    }

    /// <summary>
    /// A page holding a good id must not be emptied by a later navigation that carried none —
    /// that would take a working screen down for no reason.
    /// </summary>
    [Fact]
    public void KeepsAGoodIdWhenAnUnusableValueArrives()
    {
        var route = new MemberRoute();
        route.Accept(Member.ToString());

        Assert.False(route.Accept(null));

        Assert.Equal(Member, route.Id);
        Assert.False(route.IsMissing);
    }

    [Fact]
    public void MissingMessageDoesNotEchoTheApiRefusal()
    {
        Assert.DoesNotContain("not found", MemberRoute.MissingMessage, StringComparison.OrdinalIgnoreCase);
    }
}
