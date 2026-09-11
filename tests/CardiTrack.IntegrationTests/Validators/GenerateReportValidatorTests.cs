using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;

namespace CardiTrack.IntegrationTests.Validators;

/// <summary>
/// The business rules the reports endpoint shipped without: until now only model binding stood
/// between a request and a query with no ceiling on members or days.
/// </summary>
public class GenerateReportValidatorTests
{
    private readonly GenerateReportValidator _validator = new();

    private static GenerateReportRequest Build(
        IReadOnlyList<Guid>? memberIds = null,
        DateOnly? from = null,
        DateOnly? to = null,
        ReportFormat format = ReportFormat.Pdf,
        bool includeMetrics = true,
        bool includeAlerts = true,
        bool includeDevices = false,
        bool includeJournals = false,
        bool includeNotices = false,
        DateOnly? journalEntryDate = null,
        DigestAudience? journalAudience = null,
        string? consentToken = "consent-token") => new()
        {
            CardiMemberIds = memberIds ?? [Guid.NewGuid()],
            DateRangeFrom = from ?? new DateOnly(2026, 2, 7),
            DateRangeTo = to ?? new DateOnly(2026, 3, 9),
            Format = format,
            IncludeMetrics = includeMetrics,
            IncludeAlerts = includeAlerts,
            IncludeDevices = includeDevices,
            IncludeJournals = includeJournals,
            IncludeNotices = includeNotices,
            JournalEntryDate = journalEntryDate,
            JournalAudience = journalAudience,
            ConsentToken = consentToken
        };

    [Fact]
    public void Accepts_ATypicalRequest()
    {
        Assert.True(_validator.Validate(Build()).IsValid);
    }

    // ── Member count ────────────────────────────────────────────────────────────

    [Fact]
    public void Rejects_ANullMemberList_WithoutThrowing()
    {
        // `required` is satisfied by an explicit JSON null, so {"cardiMemberIds": null} reaches
        // here as null. Without Cascade.Stop the Must predicates ran against it and threw, which
        // the API surfaced as a 500 — the one outcome a request validator exists to prevent.
        // Built directly, not through Build(): that helper substitutes a valid list for null,
        // which would make this assert nothing.
        var request = new GenerateReportRequest
        {
            CardiMemberIds = null!,
            DateRangeFrom = new DateOnly(2026, 2, 7),
            DateRangeTo = new DateOnly(2026, 3, 9),
            Format = ReportFormat.Pdf,
            ConsentToken = "consent-token"
        };

        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_AnEmptyMemberList()
    {
        Assert.False(_validator.Validate(Build(memberIds: [])).IsValid);
    }

    [Fact]
    public void Accepts_TheMaximumMemberCount()
    {
        var ids = Enumerable.Range(0, GenerateReportValidator.MaxCardiMembers)
            .Select(_ => Guid.NewGuid()).ToList();

        Assert.True(_validator.Validate(Build(memberIds: ids)).IsValid);
    }

    [Fact]
    public void Rejects_OneMemberPastTheMaximum()
    {
        var ids = Enumerable.Range(0, GenerateReportValidator.MaxCardiMembers + 1)
            .Select(_ => Guid.NewGuid()).ToList();

        Assert.False(_validator.Validate(Build(memberIds: ids)).IsValid);
    }

    [Fact]
    public void Rejects_TheSameMemberTwice()
    {
        // Otherwise the export renders the member twice and the file-size estimate lies.
        var id = Guid.NewGuid();

        Assert.False(_validator.Validate(Build(memberIds: [id, id])).IsValid);
    }

    // ── Date range ──────────────────────────────────────────────────────────────

    [Fact]
    public void Rejects_AnEndDateBeforeTheStart()
    {
        var result = _validator.Validate(
            Build(from: new DateOnly(2026, 3, 9), to: new DateOnly(2026, 2, 7)));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Accepts_ASingleDay()
    {
        var day = new DateOnly(2026, 2, 7);

        Assert.True(_validator.Validate(Build(from: day, to: day)).IsValid);
    }

    [Fact]
    public void Accepts_TheLongestAllowedRange()
    {
        var from = new DateOnly(2026, 1, 1);
        var to = from.AddDays(GenerateReportValidator.MaxRangeDays - 1);

        Assert.True(_validator.Validate(Build(from: from, to: to)).IsValid);
    }

    [Fact]
    public void Rejects_OneDayPastTheLongestAllowedRange()
    {
        var from = new DateOnly(2026, 1, 1);
        var to = from.AddDays(GenerateReportValidator.MaxRangeDays);

        Assert.False(_validator.Validate(Build(from: from, to: to)).IsValid);
    }

    [Fact]
    public void ReportsOnlyTheOrderingError_WhenTheDatesAreReversed()
    {
        // A reversed range would otherwise also trip the length rule with a negative span, and
        // the caregiver would be told two things when only one is wrong.
        var result = _validator.Validate(
            Build(from: new DateOnly(2026, 3, 9), to: new DateOnly(2026, 2, 7)));

        Assert.Single(result.Errors);
        Assert.Contains("on or after", result.Errors[0].ErrorMessage);
    }

    // ── Format ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ReportFormat.Pdf)]
    [InlineData(ReportFormat.Csv)]
    [InlineData(ReportFormat.FhirR4)]
    public void Accepts_EveryMvp1Format(ReportFormat format)
    {
        Assert.True(_validator.Validate(Build(format: format)).IsValid);
    }

    [Fact]
    public void Rejects_Hl7V2_WhichIsAnMvp2Format()
    {
        // Defined in the enum but unrendered. Without this it would be accepted here and fail
        // later as a generation error the caregiver could do nothing about.
        Assert.False(_validator.Validate(Build(format: ReportFormat.Hl7V2)).IsValid);
    }

    [Fact]
    public void Rejects_AFormatOutsideTheEnum()
    {
        Assert.False(_validator.Validate(Build(format: (ReportFormat)99)).IsValid);
    }

    // ── FHIR section coverage ───────────────────────────────────────────────────

    [Fact]
    public void Rejects_AFhirRequestCarryingOnlyAlerts()
    {
        // FHIR R4 doesn't carry alerts in MVP 1, so this would render a lone Patient resource —
        // a "successful" export missing the only thing asked for.
        var result = _validator.Validate(Build(
            format: ReportFormat.FhirR4,
            includeMetrics: false, includeAlerts: true, includeDevices: false));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Accepts_AFhirRequestWithAlertsAlongsideReadings()
    {
        // Ticking alerts as well is fine — the readings still arrive, and M1-17's format card
        // says what FHIR carries. Only the carries-nothing case is refused.
        var result = _validator.Validate(Build(
            format: ReportFormat.FhirR4,
            includeMetrics: true, includeAlerts: true, includeDevices: false));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Accepts_AFhirRequestCarryingOnlyDevices()
    {
        Assert.True(_validator.Validate(Build(
            format: ReportFormat.FhirR4,
            includeMetrics: false, includeAlerts: false, includeDevices: true)).IsValid);
    }

    [Fact]
    public void Accepts_APdfRequestCarryingOnlyAlerts()
    {
        // The FHIR rule is scoped to FHIR: the PDF and CSV do render alerts.
        Assert.True(_validator.Validate(Build(
            format: ReportFormat.Pdf,
            includeMetrics: false, includeAlerts: true, includeDevices: false)).IsValid);
    }

    // ── Sections ────────────────────────────────────────────────────────────────

    [Fact]
    public void Rejects_ARequestWithEverySectionOff()
    {
        // It would produce a file with a header and nothing under it.
        var result = _validator.Validate(
            Build(includeMetrics: false, includeAlerts: false, includeDevices: false));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Accepts_ARequestWithOnlyDevices()
    {
        Assert.True(_validator.Validate(
            Build(includeMetrics: false, includeAlerts: false, includeDevices: true)).IsValid);
    }

    [Fact]
    public void Accepts_APdfRequestCarryingOnlyJournals()
    {
        Assert.True(_validator.Validate(Build(
            includeMetrics: false, includeAlerts: false, includeDevices: false,
            includeJournals: true)).IsValid);
    }

    [Fact]
    public void Rejects_AFhirRequestCarryingOnlyJournals()
    {
        Assert.False(_validator.Validate(Build(
            format: ReportFormat.FhirR4,
            includeMetrics: false, includeAlerts: false, includeDevices: false,
            includeJournals: true)).IsValid);
    }

    [Fact]
    public void Rejects_AMissingConsentToken()
    {
        Assert.False(_validator.Validate(Build(consentToken: null)).IsValid);
        Assert.False(_validator.Validate(Build(consentToken: "")).IsValid);
    }

    [Theory]
    [InlineData(DigestAudience.Family)]
    [InlineData(DigestAudience.Wearer)]
    public void Rejects_ALiveGlanceJournalAudience(DigestAudience audience)
    {
        var result = _validator.Validate(Build(
            includeJournals: true, journalAudience: audience));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("live glance"));
    }

    [Theory]
    [InlineData(DigestAudience.Daybook)]
    [InlineData(DigestAudience.Weekbook)]
    [InlineData(DigestAudience.Monthbook)]
    public void Accepts_AFinishedJournalBook(DigestAudience audience)
    {
        Assert.True(_validator.Validate(Build(
            includeJournals: true, journalAudience: audience)).IsValid);
    }

    [Fact]
    public void Rejects_AJournalDayOutsideTheRange()
    {
        var result = _validator.Validate(Build(
            from: new DateOnly(2026, 2, 7),
            to: new DateOnly(2026, 3, 9),
            includeJournals: true,
            journalEntryDate: new DateOnly(2026, 1, 1),
            journalAudience: DigestAudience.Daybook));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("inside the date range"));
    }

    [Fact]
    public void Accepts_AJournalDayOnTheRangeBoundary()
    {
        var from = new DateOnly(2026, 2, 7);
        Assert.True(_validator.Validate(Build(
            from: from,
            to: new DateOnly(2026, 3, 9),
            includeJournals: true,
            journalEntryDate: from,
            journalAudience: DigestAudience.Daybook)).IsValid);
    }

    [Fact]
    public void Rejects_AJournalScopeWhenJournalsAreOff()
    {
        var result = _validator.Validate(Build(
            includeJournals: false,
            journalAudience: DigestAudience.Daybook));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("when journals are included"));
    }

    [Fact]
    public void Reuse_AcceptsASnapshotWithoutAToken()
    {
        var reuse = new ReuseExportConsentValidator();
        Assert.True(reuse.Validate(Build(consentToken: null)).IsValid);
        Assert.True(reuse.Validate(Build(consentToken: "")).IsValid);
    }
}
