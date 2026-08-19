using CardiTrack.Application.DTOs.Requests;

namespace CardiTrack.Mobile.Core.Media;

/// <summary>
/// What the M1-14 edit form intends for the member's photo: keep it, replace it, or remove
/// it. One value instead of the request's two fields because the API rejects
/// <c>photoBase64</c> and <c>removePhoto</c> arriving together as a client bug — this type
/// makes that combination unrepresentable, so the form can change its mind between replace
/// and remove any number of times and only the final intent reaches the wire.
/// </summary>
public sealed class ProfilePhotoEdit
{
    private ProfilePhotoEdit(string? base64, bool remove)
    {
        Base64 = base64;
        RemovePhoto = remove;
    }

    /// <summary>Leave the stored photo alone — what an untouched form sends.</summary>
    public static ProfilePhotoEdit Keep { get; } = new(null, remove: false);

    /// <summary>Delete the stored photo and its blob.</summary>
    public static ProfilePhotoEdit Remove { get; } = new(null, remove: true);

    /// <summary>Replace the stored photo with <paramref name="base64"/>.</summary>
    public static ProfilePhotoEdit Replace(string base64)
    {
        // An empty payload would be sent as "keep" by the API's reading of the field —
        // a caller who meant to replace must not silently keep instead.
        if (string.IsNullOrWhiteSpace(base64))
            throw new ArgumentException("A replacement photo needs its base64 payload.", nameof(base64));
        return new(base64, remove: false);
    }

    public string? Base64 { get; }

    public bool RemovePhoto { get; }

    public bool IsKeep => Base64 is null && !RemovePhoto;

    /// <summary>
    /// Writes this intent onto the request — both photo fields, always, so applying one
    /// intent also erases whatever a previously applied intent left behind.
    /// </summary>
    public void ApplyTo(UpdateCardiMemberRequest request)
    {
        request.PhotoBase64 = Base64;
        request.RemovePhoto = RemovePhoto;
    }
}
