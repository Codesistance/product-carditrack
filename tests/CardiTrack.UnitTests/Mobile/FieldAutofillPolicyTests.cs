using System.Text.RegularExpressions;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// <c>FieldAutofill</c> lives in the MAUI head, which this project cannot reference, so the
/// policy is guarded the way the other mobile-head invariants here are — by reading the source.
/// Same mechanism as <see cref="AlertSoundAssetTests"/>.
/// </summary>
/// <remarks>
/// The crash this guards is specific and total: <c>UITextField.TextContentType</c>'s binding
/// raises <c>ArgumentNullException</c> from <c>GetNonNullHandle</c> when it is assigned nil, and
/// the assignment happens in a handler mapper — so the first opted-in Entry to be given a
/// handler takes the app down. The sign-in email is opted in, which made that every iOS launch
/// (dev, 2026-09-15, build 0.2.307). On iOS, "leave this field on the platform default" is only
/// expressible as not assigning; a nil-valued "default" is not available to assign.
/// </remarks>
public class FieldAutofillPolicyTests
{
    [Fact]
    public void IosBranch_AssignsNoNilContentType()
    {
        var ios = IosBranches();

        Assert.DoesNotContain("null", ios, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IosBranch_AssignsTheContentTypeOnlyForFieldsTheAutofillPolicyExcludes()
    {
        var lines = IosBranches().Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("TextContentType", StringComparison.Ordinal))
                continue;

            var guard = PrecedingCode(lines, i).Any(l => l.Contains("!IsEnabled(", StringComparison.Ordinal));

            Assert.True(guard,
                $"'{lines[i].Trim()}' assigns TextContentType without a '!IsEnabled(view)' guard above it. " +
                "A field that opts in to AutoFill must be left untouched — there is no value that " +
                "means 'the platform default', and assigning nil crashes the handler mapper.");
        }
    }

    [Theory]
    [InlineData("#if ANDROID", "#elif IOS")]
    [InlineData("#elif IOS", "#endif")]
    public void EachPlatformBranch_MapsEntryAndEditor(string start, string end)
    {
        var branch = Branches(Source(), start, end);

        Assert.Contains("EntryHandler.Mapper.AppendToMapping", branch, StringComparison.Ordinal);
        Assert.Contains("EditorHandler.Mapper.AppendToMapping", branch, StringComparison.Ordinal);
    }

    /// <summary>The iOS code, comments stripped — the prose below explains nil, it does not assign it.</summary>
    private static string IosBranches() => Branches(Source(), "#elif IOS", "#endif");

    private static string Branches(string source, string start, string end)
    {
        var code = Regex.Replace(source, @"^\s*///?.*$", string.Empty, RegexOptions.Multiline);
        var branches = code.Split(start, StringSplitOptions.None)
            .Skip(1)
            .Select(after => after.Split(end, StringSplitOptions.None)[0]);
        var found = string.Join('\n', branches);

        Assert.False(string.IsNullOrWhiteSpace(found),
            $"No '{start}' branch left in FieldAutofill.cs. If the policy moved, move this guard with it.");

        return found;
    }

    /// <summary>The two statements above <paramref name="index"/>, blank lines skipped.</summary>
    private static IEnumerable<string> PrecedingCode(string[] lines, int index) =>
        lines.Take(index).Reverse().Where(l => !string.IsNullOrWhiteSpace(l)).Take(2);

    private static string Source() => File.ReadAllText(Path.Combine(
        FindRepoRoot(), "src", "Presentation", "CardiTrack.Mobile", "Controls", "FieldAutofill.cs"));

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CardiTrack.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            $"Could not find CardiTrack.sln walking up from {AppContext.BaseDirectory}.");
    }
}
