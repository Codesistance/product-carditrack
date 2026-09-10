using CardiTrack.Infrastructure.Services.Reports;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The image ships fonts; nothing else does. What matters here is that startup never depends
/// on their presence, and that the chain the document declares is the one the Dockerfile
/// fills — a family named here with no file behind it is silently skipped, so a typo would
/// only ever show up as boxes in a printed name.
/// </summary>
public class ReportFontsTests
{
    [Fact]
    public void Register_IsANoOp_WhenTheDirectoryIsAbsent()
    {
        // A developer machine and the test runner have no /app/fonts; startup must not care.
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.Equal(0, ReportFonts.Register(missing));
    }

    [Fact]
    public void Register_IgnoresFilesThatAreNotFonts()
    {
        // The licence text rides along in the same directory (the OFL asks for it).
        var directory = Directory.CreateTempSubdirectory("carditrack-fonts-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "NOTO-LICENSE"), "SIL Open Font License");
            File.WriteAllText(Path.Combine(directory, "notes.txt"), "not a font");

            Assert.Equal(0, ReportFonts.Register(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Register_SkipsAFileThatWillNotLoad_RatherThanFailingStartup()
    {
        // This runs at service startup, not on a request path. A truncated copy of a face costs
        // one script its glyphs in an export; letting it throw would cost the whole API its boot.
        var directory = Directory.CreateTempSubdirectory("carditrack-fonts-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "NotoSansTruncated-Regular.ttf"), [0x00, 0x01, 0x00]);

            var exception = Record.Exception(() => ReportFonts.Register(directory));

            Assert.Null(exception);
            Assert.Equal(0, ReportFonts.Register(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Families_LeadWithLato_AndNameEachScriptOnce()
    {
        // Lato is what QuestPDF bundles and what every Latin page is set in; it must come first
        // or Noto Sans would take over the whole document wherever both are present.
        Assert.Equal("Lato", ReportFonts.Families[0]);
        Assert.Equal(ReportFonts.Families.Length, ReportFonts.Families.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("Noto Sans")]
    [InlineData("Noto Sans Arabic")]
    [InlineData("Noto Sans Hebrew")]
    [InlineData("Noto Sans Devanagari")]
    [InlineData("Noto Sans Symbols2")]
    public void Families_NameTheFacesTheDockerfileCopies(string family)
    {
        // Mirrors src/Presentation/CardiTrack.API/Dockerfile's fonts stage. The family name is
        // read from the file by QuestPDF, so it must be the font's own name, not the filename:
        // "Noto Sans Symbols2", no space, is what NotoSansSymbols2-Regular.ttf calls itself.
        Assert.Contains(family, ReportFonts.Families);
    }
}
