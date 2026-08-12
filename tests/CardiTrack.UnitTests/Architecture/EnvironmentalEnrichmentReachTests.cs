using System.Runtime.CompilerServices;

namespace CardiTrack.UnitTests.Architecture;

/// <summary>
/// The privacy mitigation this feature relies on — no code path outside the enrich job's own
/// dependency chain ever touches exercise/GPS data or the environmental client — is a claim
/// about which files reference which symbols, not something a runtime unit test on any one class
/// can prove by itself. This scans the actual source tree for it, the same "grep for the
/// violation" method the software-architect skill's conformance check uses manually
/// (.claude/skills/software-architect/references/conformance-check.md), made a repeatable test.
/// </summary>
public class EnvironmentalEnrichmentReachTests
{
    // Segments rather than a literal separator-bearing string: this test also runs on Linux CI
    // runners, where a hardcoded backslash path would never match anything and the test would
    // pass for the wrong reason (an empty allow-list "confining" a symbol nothing was found for).
    private static readonly string[][] EnvironmentalClientAllowedFiles =
    [
        // Declares the port.
        ["src", "Core", "CardiTrack.Application", "Interfaces", "Clients", "IEnvironmentalContextClient.cs"],
        // Implements it.
        ["src", "Infrastructure", "CardiTrack.Infrastructure", "ExternalClients", "Environmental", "GoogleEnvironmentalClient.cs"],
        // Registers it — the DI boundary that makes every other file's absence meaningful.
        ["src", "Infrastructure", "CardiTrack.Infrastructure", "Extensions", "EnvironmentalServiceExtensions.cs"],
        // The only consumer.
        ["src", "Infrastructure", "CardiTrack.Infrastructure", "Services", "EnvironmentalEnrichmentService.cs"],
    ];

    private static readonly string[][] ExerciseGpsMethodAllowedFiles =
    [
        // Declares the device-client contract.
        ["src", "Infrastructure", "CardiTrack.Infrastructure", "ExternalClients", "IDeviceApiClient.cs"],
        // Implements it.
        ["src", "Infrastructure", "CardiTrack.Infrastructure", "ExternalClients", "FitbitApiClient.cs"],
        // The only caller.
        ["src", "Infrastructure", "CardiTrack.Infrastructure", "Services", "EnvironmentalEnrichmentService.cs"],
    ];

    [Fact]
    public void IEnvironmentalContextClient_IsReferencedOnlyByItsSanctionedFiles()
    {
        AssertSymbolConfinedTo("IEnvironmentalContextClient", EnvironmentalClientAllowedFiles);
    }

    [Fact]
    public void ExerciseGpsMethods_AreCalledOnlyFromTheEnrichmentService()
    {
        AssertSymbolConfinedTo("GetExerciseSessionsAsync", ExerciseGpsMethodAllowedFiles);
        AssertSymbolConfinedTo("GetExerciseGpsPointAsync", ExerciseGpsMethodAllowedFiles);
    }

    [Fact]
    public void DeviceSyncService_NeverReferencesLocationOrExerciseGpsData()
    {
        // The routine sync loop 10,000 devices go through every few minutes must stay entirely
        // ignorant of this feature — that separation is why the enrichment pass runs as its own
        // pipeline job rather than inline (see IEnvironmentalEnrichmentService's remarks).
        var path = Path.Combine(RepoRoot,
            "src", "Infrastructure", "CardiTrack.Infrastructure", "Services", "DeviceSyncService.cs");
        var content = File.ReadAllText(path);

        Assert.DoesNotContain("GetExerciseSessionsAsync", content);
        Assert.DoesNotContain("GetExerciseGpsPointAsync", content);
        Assert.DoesNotContain("IEnvironmentalContextClient", content);
    }

    private static void AssertSymbolConfinedTo(string symbol, IReadOnlyCollection<string[]> allowedRelativePaths)
    {
        var srcRoot = Path.Combine(RepoRoot, "src");
        var allowedAbsolute = allowedRelativePaths
            .Select(segments => Path.Combine([RepoRoot, .. segments]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offenders = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !allowedAbsolute.Contains(f))
            .Where(f => File.ReadAllText(f).Contains(symbol, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"'{symbol}' is referenced outside its sanctioned files: {string.Join(", ", offenders)}. " +
            "If this is a deliberate new caller, add it to the allow-list in this test and confirm " +
            "the consent gate still applies at that call site.");
    }

    /// <summary>
    /// Located from this file's own compile-time path — stable regardless of the test runner's
    /// working directory (CI, `dotnet test`, an IDE runner all differ) — walking up to the
    /// directory that owns <c>CardiTrack.sln</c>.
    /// </summary>
    private static string RepoRoot
    {
        get
        {
            var directory = new FileInfo(ThisFilePath).Directory;
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CardiTrack.sln")))
                directory = directory.Parent;

            return directory?.FullName
                ?? throw new InvalidOperationException("Could not locate CardiTrack.sln above this test file.");
        }
    }

    private static string ThisFilePath => GetThisFilePath();

    private static string GetThisFilePath([CallerFilePath] string path = "") => path;
}
