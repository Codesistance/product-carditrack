using System.Globalization;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Mobile.Core.Forms;

/// <summary>
/// A rough size for an export before it is rendered, so "Export" is not a leap in the dark on a
/// metered connection.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately coarse and rounded up: an estimate that reads as precise would be a promise about
/// a file that has not been rendered yet. The per-day figures come from real exports of one
/// member over 30 days on dev (2026-09-07): PDF 64 KB, CSV 4 KB, FHIR R4 223 KB. The first two
/// matched the original guesses; FHIR came out three times over, because a bundle carries one
/// indented <c>Observation</c> per metric per day, each with its LOINC and UCUM codings, and the
/// original 2,400 bytes a day had been sized for a row, not a resource.
/// </para>
/// <para>
/// Lives in Core rather than in the page so the figures can be pinned to those measurements: the
/// next time a renderer changes shape, the test says so before a caregiver does.
/// </para>
/// </remarks>
public static class ExportSizeEstimate
{
    /// <summary>Bytes a single day adds, per format.</summary>
    public static int BytesPerDay(ReportFormat format) => format switch
    {
        ReportFormat.Csv => 120,
        ReportFormat.FhirR4 => 7_500,
        _ => 900
    };

    /// <summary>
    /// Bytes a file carries whatever the period: the PDF's cover, narrative and footer; the FHIR
    /// bundle's Patient and Device resources; a CSV's headers.
    /// </summary>
    public static int Overhead(ReportFormat format) => format switch
    {
        ReportFormat.Pdf => 40_000,
        ReportFormat.FhirR4 => 2_000,
        _ => 1_000
    };

    /// <summary>The estimate in bytes for a period of <paramref name="days"/>.</summary>
    public static long Bytes(int days, ReportFormat format) =>
        Overhead(format) + ((long)Math.Max(0, days) * BytesPerDay(format));

    private const long Kilobyte = 1_024;
    private const long Megabyte = 1_048_576;

    /// <summary>
    /// The estimate as the form shows it: "about 65 KB", or "about 1.2 MB" from a megabyte up.
    /// Rounded up at both scales, and the megabyte cut-over uses the same 1,048,576 the figure
    /// is divided by, so "about 1 MB" never sits beside a kilobyte count that would read larger.
    /// </summary>
    public static string Describe(int days, ReportFormat format)
    {
        var total = Bytes(days, format);
        if (total < Megabyte)
            return $"about {Math.Max(1, (total + Kilobyte - 1) / Kilobyte)} KB";

        // Invariant on purpose: the figure sits inside English copy, so "2,7 MB" under a
        // continental locale would read as two numbers.
        var tenthsOfMegabyte = Math.Ceiling(total * 10.0 / Megabyte);
        return $"about {(tenthsOfMegabyte / 10).ToString("0.#", CultureInfo.InvariantCulture)} MB";
    }
}
