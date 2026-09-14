using CardiTrack.Infrastructure.Security;

namespace CardiTrack.UnitTests.Security;

/// <summary>
/// The key is the whole of the authorization on an anonymous endpoint, so the cases that matter
/// are the ways it could accidentally be open: an unusable configuration that still matched
/// something, or a near-miss that matched.
/// </summary>
public class MobileDiagnosticsKeyTests
{
    private const string Key = "yLQb7f0m1Xy0k4V3z8Q9hJ2sN6pR5tW1cE4gU7aD0bM=";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("REPLACE_ME")]
    [InlineData("short-value")]
    public void UnusableConfiguration_IsDisabledAndRefusesEverything(string? configured)
    {
        var key = MobileDiagnosticsKey.FromConfiguration(configured);

        Assert.False(key.IsConfigured);
        Assert.False(key.Matches(configured));
        Assert.False(key.Matches(Key));
    }

    [Fact]
    public void ExactValue_Matches()
    {
        var key = MobileDiagnosticsKey.FromConfiguration(Key);

        Assert.True(key.IsConfigured);
        Assert.True(key.Matches(Key));
    }

    /// <summary>
    /// Secret Manager values and CI-stamped properties both pick up trailing newlines easily;
    /// neither side should have to be pristine for the other to recognise it.
    /// </summary>
    [Fact]
    public void SurroundingWhitespace_IsIgnoredOnBothSides()
    {
        var key = MobileDiagnosticsKey.FromConfiguration($" {Key}\n");

        Assert.True(key.Matches($"{Key} "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yLQb7f0m1Xy0k4V3z8Q9hJ2sN6pR5tW1cE4gU7aD0bM")]
    [InlineData("XLQb7f0m1Xy0k4V3z8Q9hJ2sN6pR5tW1cE4gU7aD0bM=")]
    [InlineData("yLQb7f0m1Xy0k4V3z8Q9hJ2sN6pR5tW1cE4gU7aD0bM=x")]
    public void AnythingElse_DoesNotMatch(string? presented)
    {
        var key = MobileDiagnosticsKey.FromConfiguration(Key);

        Assert.False(key.Matches(presented));
    }
}
