namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// The member-photos bucket and its signing knobs, bound from the <c>Storage:MemberPhotos</c>
/// section. An empty <see cref="Bucket"/> is the feature's off switch: uploads and removals
/// refuse loudly, reads degrade to null (initials avatar) — the state every local machine and
/// any environment without the Terraform-provisioned bucket runs in.
/// </summary>
public class MemberPhotoStorageOptions
{
    /// <summary>GCS bucket name; injected by Terraform as <c>Storage__MemberPhotos__Bucket</c>. Empty = feature disabled.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// Lifetime of the V4 signed GET URLs read surfaces hand out. Short on purpose: the URL is
    /// a bearer capability to a full-face photo, so a leaked one should die in minutes.
    /// </summary>
    public TimeSpan SignedUrlTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a signed URL is served from the in-memory cache before a fresh one is minted.
    /// Kept below <see cref="SignedUrlTtl"/> so a cached URL always has usable life left.
    /// </summary>
    public TimeSpan ReadUrlCacheTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Cap on the decoded upload the processor will even attempt to decode. Defaults to the
    /// published 5 MB contract (<see cref="Application.Services.ProfilePhotoBase64.MaxDecodedBytes"/>,
    /// which the request validators enforce); config can only tighten or relax the processor's
    /// own guard, not the documented API contract.
    /// </summary>
    public int MaxUploadBytes { get; set; } = Application.Services.ProfilePhotoBase64.MaxDecodedBytes;
}
