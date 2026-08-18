using System.Diagnostics.CodeAnalysis;

namespace CardiTrack.Mobile.Core.Media;

/// <summary>
/// What <see cref="ProfilePhotoUpload.PrepareAsync"/> made of the photo: base64 the API will
/// accept, or a sentence fit to show the caregiver explaining why there isn't one.
/// </summary>
public sealed class ProfilePhotoUploadResult
{
    private ProfilePhotoUploadResult(string? base64, string? error)
    {
        Base64 = base64;
        Error = error;
    }

    public static ProfilePhotoUploadResult Ready(string base64) => new(base64, null);

    public static ProfilePhotoUploadResult Failed(string error) => new(null, error);

    /// <summary>The request-ready base64 payload; null when preparation failed.</summary>
    public string? Base64 { get; }

    /// <summary>User-facing failure sentence; null on success.</summary>
    public string? Error { get; }

    [MemberNotNullWhen(true, nameof(Base64))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool Succeeded => Base64 is not null;
}
