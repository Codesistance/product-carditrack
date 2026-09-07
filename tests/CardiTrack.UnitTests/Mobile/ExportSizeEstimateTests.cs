using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The export form's size estimate, pinned to real exports of one member over 30 days on dev
/// (2026-09-07). An estimate may run over — it is meant to be rounded up — but it must never
/// promise a fraction of the file that arrives, which is what the FHIR figure did before this.
/// </summary>
public class ExportSizeEstimateTests
{
    private const int MeasuredDays = 30;

    [Theory]
    [InlineData(ReportFormat.Pdf, 64 * 1024)]
    [InlineData(ReportFormat.Csv, 4 * 1024)]
    [InlineData(ReportFormat.FhirR4, 223 * 1024)]
    public void ThirtyDays_IsNeverEstimatedUnderWhatDevProduced(ReportFormat format, long measuredBytes)
    {
        var estimate = ExportSizeEstimate.Bytes(MeasuredDays, format);

        // Within a fifth under and double over: coarse on purpose, but honest about the order.
        Assert.InRange(estimate, measuredBytes * 0.8, measuredBytes * 2.0);
    }

    [Fact]
    public void Fhir_Describes30DaysInTheRightHundreds()
    {
        var text = ExportSizeEstimate.Describe(MeasuredDays, ReportFormat.FhirR4);

        Assert.StartsWith("about ", text);
        Assert.EndsWith(" KB", text);
        var kb = int.Parse(text.Replace("about ", string.Empty).Replace(" KB", string.Empty));
        Assert.InRange(kb, 180, 440);
    }

    [Fact]
    public void AYearOfFhir_ReadsInMegabytes()
    {
        Assert.EndsWith(" MB", ExportSizeEstimate.Describe(365, ReportFormat.FhirR4));
    }

    [Fact]
    public void ZeroDays_IsStillAtLeastAKilobyte()
    {
        Assert.Equal("about 1 KB", ExportSizeEstimate.Describe(0, ReportFormat.Csv));
    }
}
