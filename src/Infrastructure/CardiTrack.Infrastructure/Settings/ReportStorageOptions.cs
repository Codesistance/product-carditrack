namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// The health-data export bucket and its retention knob, bound from the <c>Storage:Reports</c>
/// section. An empty <see cref="Bucket"/> is the feature's off switch — the state every local
/// machine without the Terraform-provisioned bucket runs in.
/// </summary>
/// <remarks>
/// Unlike <see cref="MemberPhotoStorageOptions"/>, there is no read-degradation here: an export is
/// the whole point of the request, so an unconfigured bucket refuses loudly on read as well as on
/// write. A caregiver told "we couldn't produce your export" is better served than one handed a
/// screen that silently shows nothing.
/// </remarks>
public class ReportStorageOptions
{
    /// <summary>GCS bucket name; injected by Terraform as <c>Storage__Reports__Bucket</c>. Empty = feature disabled.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// How long a generated export stays downloadable before the cleanup worker reaps the object
    /// and its row. Seven days, not the one hour the cache-backed design happened to enforce: that
    /// number was a Redis TTL chosen for memory pressure, and an hour is not long enough for
    /// "export it now, take it to Thursday's appointment". Kept finite because the object is an
    /// identified health record at rest — the shortest window that still serves the use case.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a report may sit <see cref="Domain.Enums.ReportStatus.Pending"/> before the
    /// cleanup worker calls it abandoned. Comfortably longer than any real generation (the AI
    /// narrative is the slow part, and its own provider timeout is far shorter), so this only ever
    /// catches a generation whose host died mid-flight.
    /// </summary>
    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromMinutes(15);
}
