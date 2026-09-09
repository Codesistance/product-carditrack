using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;

namespace CardiTrack.IntegrationTests.Validators;

public class RecordExportConsentValidatorTests
{
    private readonly RecordExportConsentValidator _validator = new();

    private static RecordExportConsentRequest Build(
        bool accepted = true,
        bool includeMetrics = true,
        ReportFormat format = ReportFormat.Pdf) => new()
    {
        CardiMemberIds = [Guid.NewGuid()],
        DateRangeFrom = new DateOnly(2026, 2, 7),
        DateRangeTo = new DateOnly(2026, 3, 9),
        Format = format,
        IncludeMetrics = includeMetrics,
        Method = ExportConsentMethod.Password,
        AcceptedResponsibility = accepted
    };

    [Fact]
    public void Accepts_ATypicalConfirmation()
    {
        Assert.True(_validator.Validate(Build()).IsValid);
    }

    [Fact]
    public void Rejects_WhenResponsibilityWasNotAccepted()
    {
        Assert.False(_validator.Validate(Build(accepted: false)).IsValid);
    }

    [Fact]
    public void Rejects_AFhirRequestWithOnlyJournalsTicked()
    {
        var request = new RecordExportConsentRequest
        {
            CardiMemberIds = [Guid.NewGuid()],
            DateRangeFrom = new DateOnly(2026, 2, 7),
            DateRangeTo = new DateOnly(2026, 3, 9),
            Format = ReportFormat.FhirR4,
            IncludeMetrics = false,
            IncludeAlerts = false,
            IncludeDevices = false,
            IncludeJournals = true,
            Method = ExportConsentMethod.Biometric,
            AcceptedResponsibility = true
        };

        Assert.False(_validator.Validate(request).IsValid);
    }
}
