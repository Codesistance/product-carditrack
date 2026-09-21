using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CardiTrack.UnitTests.Architecture;

/// <summary>
/// The MedGemma image is built from weights this repo vendors — fetched once from a pinned
/// upstream revision, hash-checked, kept in our own bucket — and a Modelfile checked in beside
/// them. That replaced pulling a Hugging Face tag at build time, which was never a pin (a
/// mutable, third-party pointer) and stopped working outright when Ollama's CVE-2026-85180 fix
/// refused the tag's cross-host blob redirect.
/// <para>
/// The guarantee now rests on a handful of files agreeing with each other: every weight the
/// Modelfile names is pinned by hash, every stage that handles the bytes checks that hash, no
/// stage reaches a registry, and the weights themselves never land in git. None of that is
/// reachable by the compiler or a runtime test, so this scans the files — the same "look for
/// the violation in the source tree" method as the sibling reach tests here.
/// </para>
/// </summary>
/// <remarks>
/// <b>What this does not do:</b> run a build. The Dockerfile was exercised end to end against
/// stand-in GGUFs when written (two real model files under the real names, a matching
/// weights.sha256, `ollama create` with both `FROM` lines, the exact-name assertion, a serving
/// container answering `/api/show` and a generate). That needs Docker and multi-GB inputs, which
/// no unit test here should. So this covers the wiring and the pins; a change to the shell in
/// the Dockerfile or the fetch script needs the stand-in build re-run by hand.
/// </remarks>
public class MedGemmaModelPinTests
{
    private static readonly string[] ModelDir = ["src", "Infrastructure", "MedGemma"];
    private static readonly string[] DockerfilePath = [.. ModelDir, "Dockerfile"];
    private static readonly string[] ModelfilePath = [.. ModelDir, "Modelfile"];
    private static readonly string[] WeightsManifestPath = [.. ModelDir, "weights.sha256"];
    private static readonly string[] WeightsEnvPath = [.. ModelDir, "weights.env"];
    private static readonly string[] ModelVersionPath = [.. ModelDir, ".model-version"];
    private static readonly string[] ModelAliasesPath = [.. ModelDir, ".model-aliases"];
    private static readonly string[] ComposePath = ["docker-compose.yml"];
    private static readonly string[][] AppSettingsWithModel =
    [
        ["src", "Presentation", "CardiTrack.API", "appsettings.json"],
        ["src", "Pipeline", "CardiTrack.PipelineJobs", "appsettings.json"],
        ["tools", "AiSplitEvaluator", "appsettings.json"],
    ];
    private static readonly string[] DeployWorkflowPath = [".github", "workflows", "deploy-medgemma-common.yml"];
    private static readonly string[] VendorWorkflowPath = [".github", "workflows", "vendor-medgemma-weights.yml"];
    private static readonly string[] FetchScriptPath = ["scripts", "fetch-medgemma-weights.sh"];
    private static readonly string[] GitIgnorePath = [".gitignore"];

    // sha256sum -c's own line format: the hash, two spaces, the file. The build runs that exact
    // tool against this exact file, so a line it would not accept is a build that cannot pass.
    private static readonly Regex ManifestLine = new(@"^[0-9a-f]{64}  [^\s/]+\.gguf$", RegexOptions.Compiled);

    [Fact]
    public void WeightsManifest_PinsEveryFileByLowercaseSha256()
    {
        var lines = NonEmptyLines(PathTo(WeightsManifestPath));

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Matches(ManifestLine, line));
    }

    [Fact]
    public void WeightsProvenance_NamesAPinnedUpstreamRevisionAndOurOwnBucket()
    {
        var env = ParseEnv(PathTo(WeightsEnvPath));

        Assert.Matches("^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$", env["HF_REPO"]);
        // A commit, not a branch: `main` would make the fetch a moving target with a fixed hash
        // beside it, which fails loudly on the next change upstream but says nothing about why.
        Assert.Matches("^[0-9a-f]{40}$", env["HF_REVISION"]);
        Assert.False(string.IsNullOrWhiteSpace(env["GCS_BUCKET"]));
        Assert.False(string.IsNullOrWhiteSpace(env["GCS_PREFIX"]));
    }

    [Fact]
    public void Modelfile_BuildsOnlyFromWeightsTheManifestPins()
    {
        var pinned = NonEmptyLines(PathTo(WeightsManifestPath))
            .Select(line => line[(line.IndexOf("  ", StringComparison.Ordinal) + 2)..])
            .ToHashSet(StringComparer.Ordinal);
        var froms = NonEmptyLines(PathTo(ModelfilePath))
            .Where(line => line.StartsWith("FROM ", StringComparison.Ordinal))
            .Select(line => line["FROM ".Length..].Trim())
            .ToList();

        Assert.NotEmpty(froms);
        Assert.All(froms, from =>
        {
            // A local file under weights/, never a registry name — the whole point is that
            // nothing in the build resolves a tag.
            Assert.StartsWith("./weights/", from);
            Assert.Contains(from["./weights/".Length..], pinned);
        });
    }

    [Fact]
    public void Dockerfile_ChecksTheHashesRegistersFromTheModelfileAndNeverPulls()
    {
        var dockerfile = File.ReadAllText(PathTo(DockerfilePath));

        // The command itself, with its argument — prose in a comment can say "sha256sum -c" and
        // would keep a bare-phrase check green after the RUN line was deleted.
        Assert.Contains("sha256sum -c ../weights.sha256", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ollama create", dockerfile, StringComparison.Ordinal);
        Assert.Contains("-f Modelfile", dockerfile, StringComparison.Ordinal);
        // Ollama lowercases a stored tag and resolves either spelling; a byte-exact `$1==tag`
        // rejected a working alias (measured on 0.34.2). The check has to match the way Ollama does.
        Assert.Contains("tolower($1)==tolower(tag)", dockerfile, StringComparison.Ordinal);
        // The two things that put a registry back into the build.
        Assert.DoesNotContain("ollama pull", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("hf.co", dockerfile, StringComparison.Ordinal);
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
        // builder stage counts, because it is what registers the weights.
        Assert.DoesNotContain(froms, from => from.Contains(":latest", StringComparison.Ordinal));
    }

    [Fact]
    public void DeployWorkflow_FetchesFromOurBucketVerifiesAndNeverReachesARegistry()
    {
        var workflow = File.ReadAllText(PathTo(DeployWorkflowPath));

        Assert.Contains("sha256sum -c ../weights.sha256", workflow, StringComparison.Ordinal);
        Assert.Contains("gcloud storage cp \"gs://", workflow, StringComparison.Ordinal);
        Assert.Contains("--build-arg MODEL_TAG=", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("hf.co", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama pull", workflow, StringComparison.Ordinal);
        // The manifest-digest guard this replaced. If it comes back, the two mechanisms will
        // disagree about what "pinned" means.
        Assert.DoesNotContain(".model-digest", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void VendorWorkflow_VerifiesThroughTheSharedScriptBeforeUploading()
    {
        var workflow = File.ReadAllText(PathTo(VendorWorkflowPath));
        var script = File.ReadAllText(PathTo(FetchScriptPath));

        // One fetch path for CI and for a developer's compose, so the two cannot verify
        // differently.
        Assert.Contains("scripts/fetch-medgemma-weights.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("gcloud storage cp", workflow, StringComparison.Ordinal);
        Assert.Contains("weights.sha256", workflow, StringComparison.Ordinal);
        Assert.Contains("sha256sum -c \"$model_dir/weights.sha256\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelVersion_IsALocalNameNotARegistryReference()
    {
        var name = File.ReadAllText(PathTo(ModelVersionPath)).Trim();

        // What `ollama create` registers and AI__Private__Model asks for. A slash would make it a
        // registry reference again; the tag part is what keeps it distinguishable in `ollama list`.
        Assert.Matches("^[a-z0-9][a-z0-9._-]*:[a-z0-9][a-z0-9._-]*$", name);
        Assert.DoesNotContain("hf.co", name, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCallerNamesTheModelTheImageRegisters()
    {
        // .model-version is what `ollama create` registers and what Terraform reads for the
        // deployed hosts. Compose and appsettings cannot read a file, so they carry the name as
        // a literal — and a literal drifts the moment the file is bumped alone, which is a 404
        // on every medical call. This is the check Terraform gets for free by reading the file.
        var name = File.ReadAllText(PathTo(ModelVersionPath)).Trim();

        var compose = File.ReadAllText(PathTo(ComposePath));
        Assert.Contains($"AI__Private__Model: \"{name}\"", compose, StringComparison.Ordinal);
        // The init container derives its name from the file rather than repeating it.
        Assert.Contains("tr -d '[:space:]' < .model-version", compose, StringComparison.Ordinal);
        Assert.DoesNotContain($"ollama create {name}", compose, StringComparison.Ordinal);
        // And checks the bytes before registering them, as the image build does.
        Assert.Contains("sha256sum -c ../weights.sha256", compose, StringComparison.Ordinal);

        foreach (var segments in AppSettingsWithModel)
        {
            var settings = File.ReadAllText(PathTo(segments));
            Assert.True(settings.Contains($"\"Model\": \"{name}\"", StringComparison.Ordinal),
                $"{Path.Combine(segments)} names a different Private model than .model-version ({name}).");
        }
    }

    [Fact]
    public void ModelAliases_AreValidNamesOtherThanTheCurrentOne_AndTheBuildRegistersThem()
    {
        var name = File.ReadAllText(PathTo(ModelVersionPath)).Trim();
        var aliases = NonEmptyLines(PathTo(ModelAliasesPath))
            .Where(line => !line.StartsWith('#'))
            .ToList();

        Assert.All(aliases, alias =>
        {
            // A name Ollama will accept for `ollama cp`, with a tag; never the current name,
            // which would be a copy onto itself.
            Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._/-]*:[A-Za-z0-9][A-Za-z0-9._-]*$", alias);
            Assert.NotEqual(name, alias);
        });

        // Whether or not a rename is in flight, the wiring that would carry one has to be there:
        // an alias line with no `ollama cp` behind it is a rollout that 404s.
        var dockerfile = File.ReadAllText(PathTo(DockerfilePath));
        var workflow = File.ReadAllText(PathTo(DeployWorkflowPath));
        Assert.Contains("ARG MODEL_ALIASES", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ollama cp \"${MODEL_TAG}\" \"${ALIAS}\"", dockerfile, StringComparison.Ordinal);
        Assert.Contains(".model-aliases", workflow, StringComparison.Ordinal);
        Assert.Contains("--build-arg \"MODEL_ALIASES=", workflow, StringComparison.Ordinal);

        // The file is comments-only whenever no rename is in flight — its normal state — and the
        // workflow's filter has to succeed on it. `grep -v` exits 1 when nothing survives, which
        // `set -e` turns into a failed build the moment the pipeline stops being an `echo`
        // argument; `sed` exits 0 with empty output.
        Assert.Contains("sed '/^[[:space:]]*#/d' src/Infrastructure/MedGemma/.model-aliases", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -v '^[[:space:]]*#' src/Infrastructure/MedGemma/.model-aliases", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeRunsTheOllamaTheImageIsBuiltOn()
    {
        // The Modelfile's two-FROM projector registration was verified on one Ollama version.
        // Compose registers from the same Modelfile, so it has to run that version — a digest
        // pinned in each file separately would drift apart on the next bump.
        var dockerfileDigest = File.ReadAllLines(PathTo(DockerfilePath))
            .Where(line => line.TrimStart().StartsWith("FROM ollama/ollama", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf("@sha256:", StringComparison.Ordinal) + "@sha256:".Length)..].Split(' ')[0])
            .Distinct()
            .Single();
        var compose = File.ReadAllText(PathTo(ComposePath));

        Assert.Contains($"image: &ollama_image ollama/ollama@sha256:{dockerfileDigest}", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void WeightsNeverReachGit()
    {
        var gitignore = File.ReadAllText(PathTo(GitIgnorePath));

        Assert.Contains("src/Infrastructure/MedGemma/weights/*.gguf", gitignore, StringComparison.Ordinal);
        // And the superseded guard's file is gone, so nobody is tempted to wire it back up.
        Assert.False(File.Exists(Path.Combine(PathTo(ModelDir), ".model-digest")),
            ".model-digest was replaced by weights.sha256; a copy here means two pins that can disagree.");
    }

    private static List<string> NonEmptyLines(string path) =>
        File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.TrimEnd()).ToList();

    private static Dictionary<string, string> ParseEnv(string path) =>
        NonEmptyLines(path)
            .Where(line => !line.StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);

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
