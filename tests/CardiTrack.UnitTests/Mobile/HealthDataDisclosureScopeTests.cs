using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The hint that silences the disclosure banner offline must belong to one caregiver: the same
/// person on the same phone, and nobody else who signs in after them.
/// </summary>
public class HealthDataDisclosureScopeTests
{
    [Fact]
    public void SameCaregiver_SameScope_RegardlessOfCaseOrWhitespace()
    {
        Assert.Equal(
            HealthDataDisclosureScope.For("Jane.Doe@Example.com"),
            HealthDataDisclosureScope.For("  jane.doe@example.com "));
    }

    [Fact]
    public void DifferentCaregivers_DifferentScopes()
    {
        Assert.NotEqual(
            HealthDataDisclosureScope.For("jane.doe@example.com"),
            HealthDataDisclosureScope.For("john.doe@example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoIdentity_NoScope_SoNothingCanBeRemembered(string? email)
    {
        Assert.Null(HealthDataDisclosureScope.For(email));
    }

    [Fact]
    public void TheScopeIsNotTheEmail()
    {
        var scope = HealthDataDisclosureScope.For("jane.doe@example.com")!;

        Assert.DoesNotContain("jane", scope, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", scope);
        Assert.Equal(64, scope.Length);
    }
}
