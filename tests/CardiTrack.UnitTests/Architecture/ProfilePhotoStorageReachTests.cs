using System.Runtime.CompilerServices;

namespace CardiTrack.UnitTests.Architecture;

/// <summary>
/// The bucket-backed designs confine the GCS SDK and the image codec to their adapter files,
/// behind the Application ports — that is what makes "no other code path can reach a bucket or
/// decode caregiver uploads" a checkable claim rather than a hope. Same source-scan mechanism as
/// <see cref="EnvironmentalEnrichmentReachTests"/>.
/// </summary>
public class ProfilePhotoStorageReachTests
{
    // Segments rather than literal separator-bearing strings — see
    // EnvironmentalEnrichmentReachTests for why (Linux CI runners).
    /// <summary>The adapters that may touch the GCS SDK's StorageClient — one bucket each.</summary>
    private static readonly string[][] GcsClientAllowedFiles =
    [
        ["src", "Infrastructure", "CardiTrack.Infrastructure", "ExternalClients", "Storage", "GcsProfilePhotoStorage.cs"],
        ["src", "Infrastructure", "CardiTrack.Infrastructure", "ExternalClients", "Storage", "GcsReportStorage.cs"],
    ];

    /// <summary>
    /// Signing is narrower than storage: only the photo adapter mints signed URLs.
    /// <c>GcsReportStorage</c> deliberately has no signing method — an export is a complete
    /// identified health record, and a signed URL to one would be a bearer capability outside the
    /// ownership check and invisible to the audit trail, so exports are streamed through the API
    /// instead. Keeping this list at one file is what stops that decision being quietly undone.
    /// </summary>
    private static readonly string[][] UrlSignerAllowedFiles =
    [
        ["src", "Infrastructure", "CardiTrack.Infrastructure", "ExternalClients", "Storage", "GcsProfilePhotoStorage.cs"],
    ];

    private static readonly string[][] ImageSharpAllowedFiles =
    [
        // The only file that may decode caregiver-uploaded bytes.
        ["src", "Infrastructure", "CardiTrack.Infrastructure", "Services", "ImageSharpProfilePhotoProcessor.cs"],
    ];

    [Fact]
    public void StorageClient_IsReferencedOnlyByTheGcsAdapters()
    {
        AssertSymbolConfinedTo("StorageClient", GcsClientAllowedFiles);
    }

    [Fact]
    public void UrlSigner_IsReferencedOnlyByThePhotoAdapter()
    {
        // The signer mints bearer-capability URLs to full-face photos; a second call site would
        // be a second place the "never log the URL" rule has to hold — and, for the export
        // bucket, a signed URL is a design the adapter deliberately does not offer at all.
        AssertSymbolConfinedTo("UrlSigner", UrlSignerAllowedFiles);
    }

    [Fact]
    public void ImageSharp_IsReferencedOnlyByTheProfilePhotoProcessor()
    {
        AssertSymbolConfinedTo("SixLabors", ImageSharpAllowedFiles);
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
            "the Tier 1 photo-handling rules (no logged URLs, content sniffing) apply there too.");
    }

    /// <summary>Same repo-root discovery as <see cref="EnvironmentalEnrichmentReachTests"/>.</summary>
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
