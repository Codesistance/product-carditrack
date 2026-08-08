using CardiTrack.Observability;
using CardiTrack.Shared;

namespace CardiTrack.UnitTests.Observability;

public class DeploymentInfoTests
{
    [Fact]
    public void Resolve_PrefersTheDeployVersionOverride()
    {
        Assert.Equal("2.0.0", DeploymentInfo.Resolve("2.0.0", "1.2.3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_FallsBackToTheStampedAssemblyVersion(string? noOverride)
    {
        Assert.Equal("1.2.3", DeploymentInfo.Resolve(noOverride, "1.2.3"));
    }

    [Fact]
    public void Resolve_ReportsUnknownWhenNothingIsStamped()
    {
        Assert.Equal(DeploymentInfo.UnknownVersion, DeploymentInfo.Resolve(null, null));
    }

    [Fact]
    public void Resolve_KeepsTheLocalMarkerOfUnstampedBuilds()
    {
        // The host projects' default <Version> — an image built outside the release
        // pipeline must not read as a release.
        Assert.Equal("0.0.0-local", DeploymentInfo.Resolve(null, "0.0.0-local"));
    }

    [Fact]
    public void Resolve_DropsTheSourceRevisionSuffixTheSdkAppends()
    {
        // The bare semver is what the backend lines telemetry up on — an image tagged
        // v1.2.3 must report 1.2.3, not 1.2.3+<sha>.
        Assert.Equal("1.2.3", DeploymentInfo.Resolve(null, "1.2.3+a1b2c3d4e5"));
    }

    [Fact]
    public void Resolve_KeepsPrereleaseLabelsWhileDroppingBuildMetadata()
    {
        Assert.Equal("1.2.3-rc.1", DeploymentInfo.Resolve(null, "1.2.3-rc.1+a1b2c3d"));
    }

    [Fact]
    public void Resolve_StripsTheLeadingVOfATagShapedOverride()
    {
        // CI tags are "v1.2.3"; an operator setting DEPLOY_VERSION will paste one.
        Assert.Equal("1.2.3", DeploymentInfo.Resolve("v1.2.3", null));
    }

    [Fact]
    public void Resolve_LeavesALeadingVThatIsPartOfTheNameAlone()
    {
        Assert.Equal("vnext", DeploymentInfo.Resolve("vnext", null));
    }

    [Fact]
    public void Resolve_TrimsSurroundingWhitespace()
    {
        // Secret Manager values and shell interpolation both like to bring a newline.
        Assert.Equal("1.2.3", DeploymentInfo.Resolve(" v1.2.3\n", null));
    }

    [Fact]
    public void ResolveEnvironment_PrefersTheDeployEnvironmentOverride()
    {
        Assert.Equal("staging", DeploymentInfo.ResolveEnvironment("Staging", "Dev"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveEnvironment_FallsBackToAspNetCoreEnvironment(string? noOverride)
    {
        Assert.Equal("dev", DeploymentInfo.ResolveEnvironment(noOverride, "Dev"));
    }

    [Theory]
    [InlineData("Dev", "dev")]
    [InlineData("Prod", "prod")]
    [InlineData(" Prod\n", "prod")]
    public void ResolveEnvironment_LowercasesWhatTerraformSets(string aspNetCoreEnvironment, string expected)
    {
        // Terraform passes title(var.environment) — "Dev"/"Prod". Environment tags are
        // case-sensitive at the backend, so "Dev" and "dev" would split one environment in two.
        Assert.Equal(expected, DeploymentInfo.ResolveEnvironment(null, aspNetCoreEnvironment));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", null)]
    public void ResolveEnvironment_ReportsNothingWhenNothingSaysWhichEnvironment(string? over, string? aspNet)
    {
        // Null, not a placeholder: an unlabelled log is obviously unlabelled, while an
        // "unknown" environment becomes a real value in the backend's environment selector.
        Assert.Null(DeploymentInfo.ResolveEnvironment(over, aspNet));
    }

    [Fact]
    public void EnvironmentName_ResolvesFromTheEnvVarsAndNeverInventsProduction()
    {
        var overrideValue = Environment.GetEnvironmentVariable(ConfigurationKeys.Deployment.Environment);
        var aspNetCoreEnvironment =
            Environment.GetEnvironmentVariable(ConfigurationKeys.Deployment.AspNetCoreEnvironment);

        Assert.Equal(
            DeploymentInfo.ResolveEnvironment(overrideValue, aspNetCoreEnvironment),
            DeploymentInfo.EnvironmentName);

        // The env vars are read raw rather than through IHostEnvironment, which answers
        // "Production" when nothing is set — a default that would file a developer
        // machine's telemetry under prod. Unset has to stay unset.
        if (string.IsNullOrWhiteSpace(overrideValue) && string.IsNullOrWhiteSpace(aspNetCoreEnvironment))
            Assert.Null(DeploymentInfo.EnvironmentName);
    }

    [Fact]
    public void Version_ReadsTheStampOnTheObservabilityAssembly()
    {
        // Not the entry assembly — that is the test runner here, and dotnet-ef under the
        // migrator job. CardiTrack.Observability always carries a <Version>, so the
        // enrichers and the OTel resource never get a blank or "unknown".
        Assert.False(string.IsNullOrWhiteSpace(DeploymentInfo.Version));
        Assert.NotEqual(DeploymentInfo.UnknownVersion, DeploymentInfo.Version);
    }
}
