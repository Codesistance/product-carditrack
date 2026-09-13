namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// Whether the caller has acknowledged the Google-mandated health-data disclosure — the
/// one-line statement of what CardiTrack collects and why, shown once per caregiver and kept
/// until they dismiss it.
/// </summary>
/// <remarks>
/// The first dismissal's timestamp is the compliance-relevant record and stays on the user
/// (<c>HealthDataDisclosureDismissedDate</c>); a client only needs to know whether to show the
/// banner, so that is all this carries.
/// </remarks>
public class HealthDataDisclosureResponse
{
    public bool Dismissed { get; set; }
}
