using System.Runtime.CompilerServices;

namespace CardiTrack.UnitTests.Architecture;

/// <summary>
/// The model pin is the only thing standing between a rebuild and a different set of weights
/// reaching caregivers: Ollama cannot pull by digest (its registry 404s digest-addressed
/// manifests and <c>ollama pull</c> rejects a <c>model@sha256:…</c> reference), so the tag stays
/// mutable and the build asserting the stored manifest's hash is the whole guarantee.
/// <para>
/// That guarantee lives in a shell block in a Dockerfile and a value in a dotfile, neither of
/// which the compiler or any runtime test can reach. This checks the parts that a later edit
/// could silently drop — the digest is recorded and well-formed, the build takes it and compares
/// it, and CI actually passes it in — the same "scan the source tree for the violation" method as
/// the sibling reach tests here.
/// </para>
/// </summary>
/// <remarks>
/// <b>What this does not do:</b> execute the guard. Its shell semantics were verified by running
/// the block against four fixtures (match, drift, no manifest, two manifests) when it was
/// written; running it from a test would mean shelling out to <c>/bin/sh</c>, which no test in
/// this repo does and which would fail on a Windows developer's machine. So this covers the
/// wiring and the recorded value — a deleted assertion, a dropped build-arg, a corrupted digest —
/// and not whether the comparison itself is still correct. A change to the block's logic needs
/// the fixtures re-run by hand.
/// </remarks>
public class MedGemmaModelPinTests
{
    private static readonly string[] DigestFile = ["src", "Infrastructure", "MedGemma", ".model-digest"];
    private static readonly string[] DockerfilePath = ["src", "Infrastructure", "MedGemma", "Dockerfile"];
    private static readonly string[] WorkflowPath = [".github", "workflows", "deploy-medgemma-common.yml"];

    [Fact]
    public void ModelDigest_IsASingleWellFormedSha256()
    {
        var digest = File.ReadAllText(PathTo(DigestFile)).Trim();

        // Lower-case hex specifically: the build compares it against `sha256sum` output as a
        // string, so an upper-cased value would fail every build rather than matching.
        Assert.Matches("^[0-9a-f]{64}$", digest);
    }

    [Fact]
    public void Dockerfile_AssertsThePulledManifestAgainstTheRecordedDigest()
    {
        var dockerfile = File.ReadAllText(PathTo(DockerfilePath));

        Assert.Contains("ARG MODEL_DIGEST", dockerfile, StringComparison.Ordinal);
        // The comparison itself, and the exit that makes it a gate rather than a warning.
        Assert.Contains("sha256sum", dockerfile, StringComparison.Ordinal);
        Assert.Contains("\"${ACTUAL}\" != \"${MODEL_DIGEST}\"", dockerfile, StringComparison.Ordinal);
        Assert.Contains("exit 1", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerfile_PinsTheOllamaBaseImageByDigestInEveryStage()
    {
        var froms = File.ReadAllLines(PathTo(DockerfilePath))
            .Where(line => line.TrimStart().StartsWith("FROM ", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(froms);
        Assert.All(froms, from => Assert.Contains("@sha256:", from, StringComparison.Ordinal));
        // A floating tag in any stage reintroduces exactly the drift the digest removes — the
        // builder stage counts, because it is what pulls the weights.
        Assert.DoesNotContain(froms, from => from.Contains(":latest", StringComparison.Ordinal));
    }

    [Fact]
    public void DeployWorkflow_PassesTheRecordedDigestIntoTheBuild()
    {
        var workflow = File.ReadAllText(PathTo(WorkflowPath));

        // Read from the file rather than written inline: a literal here would hold only until
        // someone bumped the file, which is the same reason the tag is read this way.
        Assert.Contains(".model-digest", workflow, StringComparison.Ordinal);
        Assert.Contains("--build-arg MODEL_DIGEST=", workflow, StringComparison.Ordinal);
    }

    private static string PathTo(string[] segments) => Path.Combine([RepoRoot, .. segments]);

    /// <summary>
    /// Located from this file's own compile-time path — stable regardless of the test runner's
    /// working directory (CI, <c>dotnet test</c>, an IDE runner all differ) — walking up to the
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
