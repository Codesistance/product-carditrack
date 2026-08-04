using System.Text;
using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.UnitTests.Mobile;

public class JwtPayloadReaderTests
{
    private static string Jwt(string payloadJson)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"header.{payload}.signature";
    }

    [Fact]
    public void ReadClaims_KeepsStringsAndBooleans_SkipsOtherTypes()
    {
        var claims = JwtPayloadReader.ReadClaims(Jwt(
            """{"name":"Jane Carer","email":"jane@example.com","email_verified":false,"iat":1722700000,"aud":["a","b"]}"""));

        Assert.Equal("Jane Carer", claims["name"]);
        Assert.Equal("false", claims["email_verified"]);
        Assert.False(claims.ContainsKey("iat"));
        Assert.False(claims.ContainsKey("aud"));
    }

    [Fact]
    public void ReadClaims_VerifiedTrue_SurfacesAsLowercaseTrue()
    {
        var claims = JwtPayloadReader.ReadClaims(Jwt("""{"email_verified":true}"""));

        Assert.Equal("true", claims["email_verified"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.one")]
    public void ReadClaims_GarbageInput_ReturnsEmpty(string? jwt)
    {
        Assert.Empty(JwtPayloadReader.ReadClaims(jwt));
    }
}
