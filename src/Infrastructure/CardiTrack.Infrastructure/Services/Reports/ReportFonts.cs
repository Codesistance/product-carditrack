using QuestPDF.Drawing;

namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// The typefaces a PDF export may draw with, and where the deployed image keeps them.
/// </summary>
/// <remarks>
/// <para>
/// QuestPDF bundles Lato, which covers Latin, Greek and Cyrillic and nothing else. A member's
/// name is caregiver free text in any script, and the runtime image is chiseled Ubuntu with no
/// system fonts, so without a fallback "محمد" or "मीरा" prints as a row of boxes in a document a
/// clinician reads. The API's Dockerfile copies a subset of Noto Sans into <c>/app/fonts</c>
/// (see <c>src/Presentation/CardiTrack.API/Dockerfile</c>), <see cref="Register"/> loads whatever
/// is there at startup, and <see cref="Families"/> is the chain the document declares.
/// </para>
/// <para>
/// Registering a font is not enough on its own: QuestPDF falls back only along the families a
/// text style names, in order. A family in the chain that was never registered is skipped, so
/// the same chain serves a developer machine, the test runner and the image alike; only what is
/// present gets used.
/// </para>
/// <para>
/// CJK is deliberately not in the set. Noto Sans CJK is a 20 MB collection that costs roughly
/// 70 MB of resident memory to register — a seventh of the API's Cloud Run allocation — for a
/// script the current markets rarely need in a name. Adding it is one more file in the
/// Dockerfile's COPY and one more family here.
/// </para>
/// </remarks>
public static class ReportFonts
{
    /// <summary>The directory under the application root the image copies fonts into.</summary>
    public const string Directory = "fonts";

    /// <summary>
    /// The fallback chain, most specific first: Lato for everything it has, then Noto Sans for
    /// the extended Latin, Greek and Cyrillic Lato lacks, then one Noto face per script.
    /// </summary>
    public static readonly string[] Families =
    [
        "Lato",
        "Noto Sans",
        "Noto Sans Arabic",
        "Noto Sans Hebrew",
        "Noto Sans Devanagari",
        "Noto Sans Bengali",
        "Noto Sans Gurmukhi",
        "Noto Sans Tamil",
        "Noto Sans Thai",
        "Noto Sans Armenian",
        "Noto Sans Georgian",
        "Noto Sans Symbols2",
    ];

    private static readonly string[] Extensions = [".ttf", ".otf", ".ttc"];

    /// <summary>
    /// Registers every font file in the directory with QuestPDF and returns how many it found. A
    /// missing directory is the normal case outside the image — a developer machine has its own
    /// fonts, and the tests need none — so it registers nothing rather than failing startup.
    /// </summary>
    public static int Register(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, Directory);
        if (!System.IO.Directory.Exists(directory))
            return 0;

        var registered = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;

            using var stream = File.OpenRead(file);
            FontManager.RegisterFont(stream);
            registered++;
        }

        return registered;
    }
}
