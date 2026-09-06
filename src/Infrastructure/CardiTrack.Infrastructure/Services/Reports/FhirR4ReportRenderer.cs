using System.Text;
using System.Text.Json;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
// Both namespaces define these; the FHIR meaning is the one this file is about.
using Device = Hl7.Fhir.Model.Device;
using Task = System.Threading.Tasks.Task;

namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// The interoperable export: a FHIR R4 <see cref="Bundle"/> a patient portal or EHR can ingest.
/// </summary>
/// <remarks>
/// <para>
/// This is the format with an external judge — a portal either accepts the bundle or it does not —
/// so the mapping sticks to codes those systems already recognise: LOINC for every observation,
/// UCUM for every unit, and US Core-shaped <c>Patient</c> and <c>Observation</c> resources. A
/// reading with no agreed LOINC code is left out rather than given an invented one; a bundle that
/// imports cleanly and says less is worth more than one that says everything and is rejected.
/// </para>
/// <para>
/// <strong>No free text crosses into a FHIR export</strong> — no medical notes, no alert message
/// bodies, no caregiver device labels (docs/technical/data_protection_architecture.md §70, §85).
/// Alerts appear as coded observations of the reading that triggered them, not as prose. What does
/// carry through is the member's real identity: unlike the AI narrative path, this document is
/// going to the subject's own care team, and a <c>Patient</c> resource without a name is useless
/// to the receiving system.
/// </para>
/// <para>
/// Every resource is labelled <c>R</c> (restricted) under the FHIR confidentiality vocabulary, so
/// the receiving system's own access controls start from the right assumption.
/// </para>
/// </remarks>
public class FhirR4ReportRenderer : IReportRenderer
{
    /// <summary>
    /// The SDK's System.Text.Json configuration — the supported path since Firely 5 (the older
    /// Newtonsoft <c>FhirJsonSerializer</c> is deprecated). Static because building it walks the
    /// whole R4 model, which is not work to repeat per export.
    /// </summary>
    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector).Pretty();

    private const string LoincSystem = "http://loinc.org";
    private const string UcumSystem = "http://unitsofmeasure.org";

    /// <summary>
    /// One entry per metric we can code honestly. The unit is UCUM: portals convert on these, so a
    /// wrong unit code is worse than a missing observation.
    /// </summary>
    private static readonly (Func<ActivityLog, decimal?> Value, string Loinc, string Display, string Unit, string UnitCode)[] DailyMetrics =
    [
        (l => l.Steps, "55423-8", "Number of steps in unspecified time Pedometer", "steps", "{steps}"),
        (l => l.RestingHeartRate, "40443-4", "Heart rate --resting", "beats/minute", "/min"),
        (l => l.AvgHeartRate, "8867-4", "Heart rate", "beats/minute", "/min"),
        (l => l.SpO2Average, "59408-5", "Oxygen saturation in Arterial blood by Pulse oximetry", "%", "%"),
        (l => l.SleepMinutes, "93832-4", "Sleep duration", "minutes", "min"),
        (l => l.BreathingRate, "9279-1", "Respiratory rate", "breaths/minute", "/min")
    ];

    public ReportFormat Format => ReportFormat.FhirR4;

    public Task<RenderedReport> RenderAsync(
        ReportDataSet data,
        ReportSections sections,
        string? narrative,
        CancellationToken ct = default)
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Timestamp = DateTimeOffset.UtcNow,
            Meta = RestrictedMeta()
        };

        foreach (var member in data.Members)
        {
            // The member's own id doubles as the Patient resource id: it is already a GUID, which
            // is what a urn:uuid: fullUrl requires, and it makes the subject reference stable
            // across the whole bundle.
            var patientId = member.Member.Id;
            AddEntry(bundle, patientId, BuildPatient(member.Member, patientId));

            if (sections.IncludeDevices)
            {
                foreach (var device in member.Devices)
                    AddEntry(bundle, device.Id, BuildDevice(device, patientId));
            }

            if (sections.IncludeMetrics)
            {
                foreach (var log in member.ActivityLogs)
                {
                    foreach (var observation in BuildDailyObservations(log, patientId))
                        AddEntry(bundle, Guid.NewGuid(), observation);
                }
            }
        }

        // Pretty-printed: a caregiver who opens the file to check what they are handing over
        // should be able to read it, and the size difference is irrelevant at this scale.
        var json = JsonSerializer.Serialize(bundle, FhirJson);

        return Task.FromResult(new RenderedReport(
            Encoding.UTF8.GetBytes(json), "application/fhir+json", "json"));
    }

    /// <summary>
    /// Entries carry a <c>urn:uuid:</c> fullUrl, the form a collection bundle uses for resources
    /// that have no server-assigned identity yet — which is exactly our case: these resources are
    /// minted for the export and live nowhere afterwards.
    /// </summary>
    /// <remarks>
    /// The id has to be a real UUID, not merely unique: <c>urn:uuid:</c> is a registered scheme and
    /// a strict parser rejects anything else in it. A readable id like <c>obs-…-8867-4</c> looks
    /// fine in the JSON and fails at the receiving system, so ids here are GUIDs and the human
    /// meaning lives in the LOINC code instead.
    /// </remarks>
    private static void AddEntry(Bundle bundle, Guid id, Resource resource)
    {
        resource.Id = id.ToString();
        bundle.Entry.Add(new Bundle.EntryComponent
        {
            FullUrl = ToUrn(id),
            Resource = resource
        });
    }

    private static string ToUrn(Guid id) => $"urn:uuid:{id}";

    private static Meta RestrictedMeta() => new()
    {
        Security =
        [
            new Coding("http://terminology.hl7.org/CodeSystem/v3-Confidentiality", "R", "restricted")
        ]
    };

    private static Patient BuildPatient(CardiMember member, Guid id) => new()
    {
        Id = id.ToString(),
        Meta = RestrictedMeta(),
        Active = true,
        // HumanName.Text rather than parsed given/family: we store one name field, and splitting
        // it on whitespace would guess wrong for a great many real names.
        Name = [new HumanName { Text = member.Name }],
        BirthDate = member.DateOfBirth.ToString("yyyy-MM-dd"),
        Gender = MapGender(member.Gender)
        // Deliberately absent: telecom, address, emergency contacts and medical notes. None of
        // them is needed to interpret the readings, and each is a Tier 1 identifier.
    };

    private static Device BuildDevice(DeviceConnection connection, Guid patientId) => new()
    {
        Id = connection.Id.ToString(),
        Meta = RestrictedMeta(),
        // The device *type*, never connection.DeviceName — that label is caregiver free text.
        Type = new CodeableConcept { Text = connection.DeviceType.ToString() },
        Status = connection.ConnectionStatus == ConnectionStatus.Connected
            ? Device.FHIRDeviceStatus.Active
            : Device.FHIRDeviceStatus.Inactive,
        Patient = new ResourceReference(ToUrn(patientId))
    };

    private static IEnumerable<Observation> BuildDailyObservations(ActivityLog log, Guid patientId)
    {
        foreach (var (value, loinc, display, unit, unitCode) in DailyMetrics)
        {
            if (value(log) is not { } reading)
                continue; // A metric the device did not report is absent, not zero.

            yield return new Observation
            {
                // Id is assigned by AddEntry, which owns the fullUrl it has to match.
                Meta = RestrictedMeta(),
                Status = ObservationStatus.Final,
                Category =
                [
                    new CodeableConcept(
                        "http://terminology.hl7.org/CodeSystem/observation-category",
                        "activity", "Activity", null)
                ],
                Code = new CodeableConcept(LoincSystem, loinc, display, null),
                Subject = new ResourceReference(ToUrn(patientId)),
                // The reading is a whole day's summary, so it is dated to the day rather than an
                // instant — a portal charting it at midnight would imply a precision we don't have.
                Effective = new FhirDateTime(log.Date.ToString("yyyy-MM-dd")),
                Value = new Quantity
                {
                    Value = reading,
                    Unit = unit,
                    System = UcumSystem,
                    Code = unitCode
                }
            };
        }
    }

    /// <summary>
    /// Our <see cref="Gender"/> onto FHIR's administrative gender. Anything we do not hold a
    /// confident mapping for goes to <c>unknown</c> rather than being guessed — an administrative
    /// gender is used for clinical reference ranges downstream.
    /// </summary>
    private static AdministrativeGender MapGender(Gender gender) => gender switch
    {
        Gender.Male => AdministrativeGender.Male,
        Gender.Female => AdministrativeGender.Female,
        _ => AdministrativeGender.Unknown
    };
}
