using System.Text;
using System.Text.Json;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services.Reports;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Task = System.Threading.Tasks.Task;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The three MVP 1 export formats. What is asserted here is what a recipient outside CardiTrack
/// depends on: a spreadsheet that opens with the right columns, a bundle a portal will accept, and
/// a PDF that is actually a PDF — plus the rule all three share, that free text and identifiers we
/// promised not to export stay out.
/// </summary>
public class ReportRendererTests
{
    private static readonly Guid MemberId = Guid.NewGuid();

    private static readonly ReportSections AllSections = new(
        IncludeMetrics: true, IncludeAlerts: true, IncludeDevices: true);

    static ReportRendererTests()
    {
        // The renderer's own registration does this at startup; tests construct it directly.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    private static ReportDataSet BuildData(
        string memberName = "Margaret Doe",
        IReadOnlyList<ActivityLog>? logs = null,
        IReadOnlyList<Alert>? alerts = null,
        IReadOnlyList<DeviceConnection>? devices = null) =>
        new(
            [
                new ReportMemberData(
                    new CardiMember
                    {
                        Id = MemberId,
                        Name = memberName,
                        DateOfBirth = new DateOnly(1948, 4, 12),
                        Gender = Gender.Female,
                        MedicalNotes = "Takes warfarin; history of AF"
                    },
                    logs ?? [FullDay()],
                    alerts ?? [],
                    devices ?? [])
            ],
            new DateOnly(2026, 2, 7),
            new DateOnly(2026, 3, 9),
            Title: null);

    private static ActivityLog FullDay() => new()
    {
        CardiMemberId = MemberId,
        Date = new DateOnly(2026, 2, 10),
        Steps = 4321,
        Distance = 3.2m,
        ActiveMinutes = 41,
        RestingHeartRate = 68,
        AvgHeartRate = 79,
        MinHeartRate = 52,
        MaxHeartRate = 118,
        SleepMinutes = 410,
        SleepEfficiency = 88,
        DeepSleepMinutes = 72,
        RemSleepMinutes = 95,
        SpO2Average = 96.5m,
        DataSource = DeviceType.Fitbit
    };

    /// <summary>A day the watch was not worn: every metric null.</summary>
    private static ActivityLog EmptyDay() => new()
    {
        CardiMemberId = MemberId,
        Date = new DateOnly(2026, 2, 11),
        DataSource = DeviceType.Fitbit
    };

    private static Alert BuildAlert() => new()
    {
        CardiMemberId = MemberId,
        Title = "Resting heart rate above usual",
        Message = "Margaret's resting heart rate has been higher than her baseline for three days.",
        Severity = AlertSeverity.Orange,
        AlertType = AlertType.HeartRate,
        TriggeredDate = new DateTime(2026, 2, 15, 8, 0, 0, DateTimeKind.Utc)
    };

    private static DeviceConnection BuildDevice() => new()
    {
        CardiMemberId = MemberId,
        DeviceType = DeviceType.Fitbit,
        DeviceName = "Mom's Fitbit",
        ConnectionStatus = ConnectionStatus.Connected,
        ConnectedDate = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
        LastSyncDate = new DateTime(2026, 3, 9, 6, 30, 0, DateTimeKind.Utc)
    };

    // ── CSV ─────────────────────────────────────────────────────────────────────

    private static async Task<string> RenderCsvAsync(
        ReportDataSet data, ReportSections? sections = null)
    {
        var rendered = await new CsvReportRenderer()
            .RenderAsync(data, sections ?? AllSections, narrative: null);

        return Encoding.UTF8.GetString(rendered.Content);
    }

    [Fact]
    public async Task Csv_DeclaresItselfAsCsv()
    {
        var rendered = await new CsvReportRenderer().RenderAsync(BuildData(), AllSections, null);

        Assert.Equal("text/csv; charset=utf-8", rendered.ContentType);
        Assert.Equal("csv", rendered.Extension);
    }

    [Fact]
    public async Task Csv_StartsWithAByteOrderMark()
    {
        // Without it Excel on Windows reads the file as the system codepage and mangles any
        // non-ASCII member name — the detail that decides whether this opens correctly for most
        // of the people who will open it.
        var rendered = await new CsvReportRenderer().RenderAsync(BuildData("Zoë Müller"), AllSections, null);

        Assert.Equal([0xEF, 0xBB, 0xBF], rendered.Content.Take(3));
    }

    [Fact]
    public async Task Csv_WritesOneRowPerDay_WithTheDocumentedHeader()
    {
        var csv = await RenderCsvAsync(BuildData(logs: [FullDay(), EmptyDay()]));
        var lines = csv.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        Assert.StartsWith("Member,Date,Steps,DistanceKm,ActiveMinutes,", lines[0].TrimStart('﻿'));
        Assert.StartsWith("Margaret Doe,2026-02-10,4321,3.2,41,", lines[1]);
        Assert.StartsWith("Margaret Doe,2026-02-11,,,,", lines[2]);
    }

    [Fact]
    public async Task Csv_LeavesAMissingReadingEmpty_NeverZero()
    {
        // A day the watch was not worn and a day of no steps are different facts. Writing 0 for
        // the first would put a false reading in a file a caregiver may hand to a clinician.
        var csv = await RenderCsvAsync(BuildData(logs: [EmptyDay()]));
        var row = csv.TrimEnd().Split('\n')[1].TrimEnd('\r');

        Assert.DoesNotContain("0", row.Split(',')[2]);
        Assert.Equal(string.Empty, row.Split(',')[2]);
    }

    [Fact]
    public async Task Csv_UsesInvariantNumbersAndDates()
    {
        // A decimal comma would collide with the delimiter; a localised date would be ambiguous
        // in a file the caregiver may send onward.
        var csv = await RenderCsvAsync(BuildData());

        Assert.Contains("2026-02-10", csv);
        Assert.Contains("3.2", csv);
    }

    [Fact]
    public async Task Csv_IncludesAlertTitles_ButNeverAlertMessageBodies()
    {
        // Titles come from our own rule set; message bodies are free text, which never leaves in
        // an export (data_protection_architecture.md §85).
        var csv = await RenderCsvAsync(BuildData(alerts: [BuildAlert()]));

        Assert.Contains("Resting heart rate above usual", csv);
        Assert.DoesNotContain("higher than her baseline", csv);
    }

    [Fact]
    public async Task Csv_NamesDeviceTypes_ButNeverTheCaregiversLabel()
    {
        var csv = await RenderCsvAsync(BuildData(devices: [BuildDevice()]));

        Assert.Contains("Fitbit", csv);
        Assert.DoesNotContain("Mom's Fitbit", csv);
    }

    [Fact]
    public async Task Csv_NeverCarriesMedicalNotes()
    {
        var csv = await RenderCsvAsync(BuildData());

        Assert.DoesNotContain("warfarin", csv);
    }

    [Fact]
    public async Task Csv_OmitsSectionsTheCaregiverDidNotTick()
    {
        var csv = await RenderCsvAsync(
            BuildData(alerts: [BuildAlert()], devices: [BuildDevice()]),
            new ReportSections(IncludeMetrics: true, IncludeAlerts: false, IncludeDevices: false));

        Assert.Contains("Steps", csv);
        Assert.DoesNotContain("Resting heart rate above usual", csv);
        Assert.DoesNotContain("ConnectionStatus", csv);
    }

    // ── FHIR R4 ─────────────────────────────────────────────────────────────────

    private static async Task<Bundle> RenderBundleAsync(
        ReportDataSet data, ReportSections? sections = null)
    {
        var rendered = await new FhirR4ReportRenderer()
            .RenderAsync(data, sections ?? AllSections, narrative: null);

        var options = new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

        // Round-tripping through the SDK's own parser is the assertion that matters: a bundle
        // that only looks right as JSON text is a bundle a portal may still reject.
        return JsonSerializer.Deserialize<Bundle>(
            Encoding.UTF8.GetString(rendered.Content), options)!;
    }

    [Fact]
    public async Task Fhir_DeclaresTheFhirJsonContentType()
    {
        var rendered = await new FhirR4ReportRenderer().RenderAsync(BuildData(), AllSections, null);

        Assert.Equal("application/fhir+json", rendered.ContentType);
        Assert.Equal("json", rendered.Extension);
    }

    [Fact]
    public async Task Fhir_ParsesBackAsACollectionBundle()
    {
        var bundle = await RenderBundleAsync(BuildData());

        Assert.Equal(Bundle.BundleType.Collection, bundle.Type);
        Assert.NotEmpty(bundle.Entry);
    }

    [Fact]
    public async Task Fhir_DeserialisesStrictly_WithoutRaisingIssues()
    {
        // The closest check to the receiving system's that runs without a network call. The SDK's
        // System.Text.Json path is strict: it raises DeserializationFailedException, listing every
        // issue, wherever the JSON departs from the R4 model — where a permissive parse would
        // quietly tolerate it and leave the rejection to the portal.
        var rendered = await new FhirR4ReportRenderer()
            .RenderAsync(BuildData(alerts: [BuildAlert()], devices: [BuildDevice()]), AllSections, null);

        var options = new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

        var exception = Record.Exception(() => JsonSerializer.Deserialize<Bundle>(
            Encoding.UTF8.GetString(rendered.Content), options));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Fhir_CarriesThePatientsRealIdentity()
    {
        // Unlike the AI narrative path, this document goes to the subject's own care team: a
        // Patient resource without a name is useless to the receiving system.
        var bundle = await RenderBundleAsync(BuildData());

        var patient = bundle.Entry.Select(e => e.Resource).OfType<Patient>().Single();
        Assert.Equal("Margaret Doe", patient.Name.Single().Text);
        Assert.Equal("1948-04-12", patient.BirthDate);
        Assert.Equal(AdministrativeGender.Female, patient.Gender);
    }

    [Fact]
    public async Task Fhir_LabelsEveryResourceAsRestricted()
    {
        var bundle = await RenderBundleAsync(BuildData(devices: [BuildDevice()]));

        Assert.All(
            bundle.Entry,
            entry => Assert.Contains(
                entry.Resource!.Meta!.Security,
                coding => coding.Code == "R"
                    && coding.System == "http://terminology.hl7.org/CodeSystem/v3-Confidentiality"));
    }

    [Fact]
    public async Task Fhir_CodesEveryObservationWithLoincAndUcum()
    {
        var bundle = await RenderBundleAsync(BuildData());
        var observations = bundle.Entry.Select(e => e.Resource).OfType<Observation>().ToList();

        Assert.NotEmpty(observations);
        Assert.All(observations, o =>
        {
            var coding = Assert.Single(o.Code.Coding);
            Assert.Equal("http://loinc.org", coding.System);
            Assert.False(string.IsNullOrEmpty(coding.Code));

            var quantity = Assert.IsType<Quantity>(o.Value);
            Assert.Equal("http://unitsofmeasure.org", quantity.System);
            Assert.False(string.IsNullOrEmpty(quantity.Code));
        });
    }

    [Theory]
    [InlineData("55423-8")] // steps
    [InlineData("40443-4")] // resting heart rate
    [InlineData("8867-4")]  // heart rate
    [InlineData("59408-5")] // SpO2
    [InlineData("93832-4")] // sleep duration
    public async Task Fhir_EmitsTheExpectedLoincCode(string loinc)
    {
        var bundle = await RenderBundleAsync(BuildData());

        Assert.Contains(
            bundle.Entry.Select(e => e.Resource).OfType<Observation>(),
            o => o.Code.Coding.Any(c => c.Code == loinc));
    }

    [Fact]
    public async Task Fhir_OmitsAMetricTheDeviceNeverReported()
    {
        // Absent, not zero: a portal charting a fabricated zero would show a clinical event that
        // never happened.
        var bundle = await RenderBundleAsync(BuildData(logs: [EmptyDay()]));

        Assert.Empty(bundle.Entry.Select(e => e.Resource).OfType<Observation>());
    }

    [Fact]
    public async Task Fhir_DatesADailySummaryToTheDay()
    {
        var bundle = await RenderBundleAsync(BuildData());
        var observation = bundle.Entry.Select(e => e.Resource).OfType<Observation>().First();

        // Not an instant — charting a whole day's summary at midnight would imply a precision
        // the reading does not have.
        Assert.Equal("2026-02-10", Assert.IsType<FhirDateTime>(observation.Effective).Value);
    }

    [Fact]
    public async Task Fhir_CarriesNoFreeTextAnywhere()
    {
        var rendered = await new FhirR4ReportRenderer()
            .RenderAsync(BuildData(alerts: [BuildAlert()], devices: [BuildDevice()]), AllSections, null);
        var json = Encoding.UTF8.GetString(rendered.Content);

        Assert.DoesNotContain("warfarin", json);              // medical notes
        Assert.DoesNotContain("Mom's Fitbit", json);          // caregiver device label
        Assert.DoesNotContain("higher than her baseline", json); // alert message body
    }

    [Fact]
    public async Task Fhir_IgnoresTheNarrative_EvenWhenOneIsOffered()
    {
        var rendered = await new FhirR4ReportRenderer()
            .RenderAsync(BuildData(), AllSections, "A paragraph of generated English.");

        Assert.DoesNotContain(
            "generated English", Encoding.UTF8.GetString(rendered.Content));
    }

    [Fact]
    public async Task Fhir_LinksEveryObservationToThePatient()
    {
        var bundle = await RenderBundleAsync(BuildData());
        var patient = bundle.Entry.Select(e => e.Resource).OfType<Patient>().Single();

        Assert.All(
            bundle.Entry.Select(e => e.Resource).OfType<Observation>(),
            o => Assert.Equal($"urn:uuid:{patient.Id}", o.Subject!.Reference));
    }

    // ── PDF ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pdf_DeclaresThePdfContentType()
    {
        var rendered = await new PdfReportRenderer().RenderAsync(BuildData(), AllSections, "Summary.");

        Assert.Equal("application/pdf", rendered.ContentType);
        Assert.Equal("pdf", rendered.Extension);
    }

    [Fact]
    public async Task Pdf_ProducesARealPdf()
    {
        var rendered = await new PdfReportRenderer().RenderAsync(BuildData(), AllSections, "Summary.");

        Assert.Equal("%PDF", Encoding.ASCII.GetString(rendered.Content, 0, 4));
        Assert.True(rendered.Content.Length > 1000, "A one-member export should not be a stub.");
    }

    [Fact]
    public async Task Pdf_RendersWithoutANarrative()
    {
        // Generation only asks the model for PDFs, but a caller passing null must not fault.
        var rendered = await new PdfReportRenderer().RenderAsync(BuildData(), AllSections, narrative: null);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(rendered.Content, 0, 4));
    }

    [Fact]
    public async Task Pdf_RendersAMemberWithNoReadingsAtAll()
    {
        // The empty-period case is real: a member whose device was never connected in the range.
        var rendered = await new PdfReportRenderer()
            .RenderAsync(BuildData(logs: []), AllSections, "Summary.");

        Assert.Equal("%PDF", Encoding.ASCII.GetString(rendered.Content, 0, 4));
    }

    [Fact]
    public async Task Pdf_RendersALongPeriodAcrossPages()
    {
        var logs = Enumerable.Range(0, 200)
            .Select(i => new ActivityLog
            {
                CardiMemberId = MemberId,
                Date = new DateOnly(2026, 1, 1).AddDays(i),
                Steps = 3000 + i,
                RestingHeartRate = 60 + i % 10
            })
            .ToList();

        var rendered = await new PdfReportRenderer()
            .RenderAsync(BuildData(logs: logs), AllSections, "Summary.");

        Assert.Equal("%PDF", Encoding.ASCII.GetString(rendered.Content, 0, 4));
    }
}
