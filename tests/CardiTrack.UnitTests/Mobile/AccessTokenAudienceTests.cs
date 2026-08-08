using System.Text;
using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.UnitTests.Mobile;

public class AccessTokenAudienceTests
{
    private const string Api = "https://api.carditrack.com";

    /// <summary>An unsigned JWT with the given payload — nothing here validates signatures.</summary>
    private static string Jwt(string payloadJson) =>
        $"{Base64Url("""{"alg":"RS256","typ":"JWT"}""")}.{Base64Url(payloadJson)}.signature";

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Read_ReturnsSingleAudience_WhenAudIsAString()
    {
        var token = Jwt($$"""{"aud":"{{Api}}","sub":"auth0|1"}""");

        Assert.Equal([Api], AccessTokenAudience.Read(token));
    }

    [Fact]
    public void Read_ReturnsEveryAudience_WhenAudIsAnArray()
    {
        // Auth0 issues an array whenever the token also carries the /userinfo audience —
        // the shape JwtPayloadReader.ReadClaims drops, and the common real-world case.
        var token = Jwt($$"""{"aud":["{{Api}}","https://tenant.eu.auth0.com/userinfo"]}""");

        Assert.Equal([Api, "https://tenant.eu.auth0.com/userinfo"], AccessTokenAudience.Read(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.!!!not-base64!!!.c")]
    public void Read_ReturnsNothing_ForTokensThatArentReadableJwts(string? token)
    {
        Assert.Empty(AccessTokenAudience.Read(token));
    }

    [Fact]
    public void Read_ReturnsNothing_WhenTheAudClaimIsMissing()
    {
        Assert.Empty(AccessTokenAudience.Read(Jwt("""{"sub":"auth0|1"}""")));
    }

    [Fact]
    public void Check_MatchesOnlyExactAudiences()
    {
        Assert.Equal(AudienceCheck.Match,
            AccessTokenAudience.Check(Jwt($$"""{"aud":"{{Api}}"}"""), Api));
        Assert.Equal(AudienceCheck.Mismatch,
            AccessTokenAudience.Check(Jwt("""{"aud":"https://api.dev.carditrack.com"}"""), Api));
        // Auth0 audiences are case-sensitive identifiers, not URLs to normalise.
        Assert.Equal(AudienceCheck.Mismatch,
            AccessTokenAudience.Check(Jwt("""{"aud":"https://API.carditrack.com"}"""), Api));
    }

    [Fact]
    public void Check_MatchesWhenTheApiIsOneOfSeveralAudiences()
    {
        var token = Jwt($$"""{"aud":["https://tenant.eu.auth0.com/userinfo","{{Api}}"]}""");

        Assert.Equal(AudienceCheck.Match, AccessTokenAudience.Check(token, Api));
    }

    [Fact]
    public void Check_ReportsUnreadable_ForOpaqueTokens()
    {
        // What Auth0 returns when no API audience was requested — the failure this exists to name.
        Assert.Equal(AudienceCheck.Unreadable, AccessTokenAudience.Check("cVOc9Fn1gGz9Yj", Api));
    }

    [Fact]
    public void Describe_NamesTheAudiences_ForTheLog()
    {
        Assert.Equal(Api, AccessTokenAudience.Describe(Jwt($$"""{"aud":"{{Api}}"}""")));
        Assert.Equal("(none)", AccessTokenAudience.Describe("opaque"));
    }
}
