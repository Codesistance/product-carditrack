using System.Text.RegularExpressions;
using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.UnitTests.Mobile;

public class PkceTests
{
    // 32 random bytes → 43 base64url chars, no padding.
    private static readonly Regex Base64Url43 = new("^[A-Za-z0-9_-]{43}$");

    [Fact]
    public void CreateChallenge_MatchesRfc7636KnownVector()
    {
        // RFC 7636 Appendix B — the canonical verifier/challenge pair.
        var challenge = Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
    }

    [Fact]
    public void CreateVerifier_IsUrlSafeBase64_WithoutPadding()
    {
        Assert.Matches(Base64Url43, Pkce.CreateVerifier());
    }

    [Fact]
    public void CreateState_IsUrlSafeBase64_WithoutPadding()
    {
        Assert.Matches(Base64Url43, Pkce.CreateState());
    }

    [Fact]
    public void SuccessiveValues_Differ()
    {
        Assert.NotEqual(Pkce.CreateVerifier(), Pkce.CreateVerifier());
        Assert.NotEqual(Pkce.CreateState(), Pkce.CreateState());
    }
}
