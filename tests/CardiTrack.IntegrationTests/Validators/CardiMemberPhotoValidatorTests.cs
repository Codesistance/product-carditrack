using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.IntegrationTests.Validators;

/// <summary>
/// What the two CardiMember forms will accept as a profile photo payload. The validators judge
/// only shape (valid base64) and the published 5 MB decoded cap — whether the bytes are a real
/// JPEG/PNG is decided once, by the processor's content sniffing — plus the one combination
/// rule: a new photo and a removal cannot arrive on the same update.
/// </summary>
public class CardiMemberPhotoValidatorTests
{
    private readonly CreateCardiMemberValidator _create = new();
    private readonly UpdateCardiMemberValidator _update = new();

    // Old enough to clear the 18–120 rule, so a failure here is always about the photo.
    private static readonly DateOnly Dob = new(1955, 6, 15);

    private static CreateCardiMemberRequest CreateRequest(string? photoBase64) =>
        new() { Name = "Margaret Doe", DateOfBirth = Dob, Gender = Gender.Female, PhotoBase64 = photoBase64 };

    private static UpdateCardiMemberRequest UpdateRequest(string? photoBase64, bool removePhoto = false) =>
        new() { Name = "Margaret Doe", DateOfBirth = Dob, PhotoBase64 = photoBase64, RemovePhoto = removePhoto };

    private static string Base64OfLength(int decodedBytes) =>
        Convert.ToBase64String(new byte[decodedBytes]);

    [Fact]
    public void Create_AcceptsAnOmittedPhoto()
    {
        Assert.True(_create.Validate(CreateRequest(null)).IsValid);
    }

    [Fact]
    public void Create_AcceptsValidBase64()
    {
        Assert.True(_create.Validate(CreateRequest(Base64OfLength(64))).IsValid);
    }

    /// <summary>
    /// The documented examples carry a data-URI prefix, so a client pasting one straight from an
    /// image picker must not be bounced on a technicality.
    /// </summary>
    [Fact]
    public void Create_AcceptsADataUriPrefix()
    {
        Assert.True(_create.Validate(CreateRequest($"data:image/jpeg;base64,{Base64OfLength(64)}")).IsValid);
    }

    [Fact]
    public void Create_RejectsInvalidBase64()
    {
        var result = _create.Validate(CreateRequest("not base64!!!"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateCardiMemberRequest.PhotoBase64));
    }

    [Fact]
    public void Create_RejectsAPhotoOverTheCap()
    {
        var result = _create.Validate(CreateRequest(Base64OfLength(ProfilePhotoBase64.MaxDecodedBytes + 1)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("5 MB"));
    }

    [Fact]
    public void Create_AcceptsAPhotoExactlyAtTheCap()
    {
        Assert.True(_create.Validate(CreateRequest(Base64OfLength(ProfilePhotoBase64.MaxDecodedBytes))).IsValid);
    }

    [Fact]
    public void Update_AcceptsAnOmittedPhoto()
    {
        Assert.True(_update.Validate(UpdateRequest(null)).IsValid);
    }

    [Fact]
    public void Update_AcceptsANewPhoto()
    {
        Assert.True(_update.Validate(UpdateRequest(Base64OfLength(64))).IsValid);
    }

    [Fact]
    public void Update_AcceptsRemovePhotoAlone()
    {
        Assert.True(_update.Validate(UpdateRequest(null, removePhoto: true)).IsValid);
    }

    [Fact]
    public void Update_RejectsInvalidBase64()
    {
        var result = _update.Validate(UpdateRequest("not base64!!!"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateCardiMemberRequest.PhotoBase64));
    }

    [Fact]
    public void Update_RejectsAPhotoOverTheCap()
    {
        Assert.False(_update.Validate(UpdateRequest(Base64OfLength(ProfilePhotoBase64.MaxDecodedBytes + 1))).IsValid);
    }

    /// <summary>
    /// Contradictory intent is refused rather than resolved — see UpdateCardiMemberValidator.
    /// The failure names RemovePhoto so a client maps the error onto the toggle, not the picker.
    /// </summary>
    [Fact]
    public void Update_RejectsANewPhotoAndRemoveTogether()
    {
        var result = _update.Validate(UpdateRequest(Base64OfLength(64), removePhoto: true));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateCardiMemberRequest.RemovePhoto));
    }
}
