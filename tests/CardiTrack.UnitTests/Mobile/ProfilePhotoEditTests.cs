using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Mobile.Core.Media;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The API rejects <c>photoBase64</c> and <c>removePhoto</c> arriving together as a client
/// bug — these tests are the client's proof it can never be that client.
/// </summary>
public sealed class ProfilePhotoEditTests
{
    [Fact]
    public void Keep_SendsNeitherField()
    {
        var request = new UpdateCardiMemberRequest();

        ProfilePhotoEdit.Keep.ApplyTo(request);

        Assert.Null(request.PhotoBase64);
        Assert.False(request.RemovePhoto);
    }

    [Fact]
    public void Replace_SendsThePayloadAndNotTheRemoval()
    {
        var request = new UpdateCardiMemberRequest();

        ProfilePhotoEdit.Replace("aGVsbG8=").ApplyTo(request);

        Assert.Equal("aGVsbG8=", request.PhotoBase64);
        Assert.False(request.RemovePhoto);
    }

    [Fact]
    public void Remove_SendsTheRemovalAndNoPayload()
    {
        var request = new UpdateCardiMemberRequest();

        ProfilePhotoEdit.Remove.ApplyTo(request);

        Assert.Null(request.PhotoBase64);
        Assert.True(request.RemovePhoto);
    }

    [Fact]
    public void TheLastIntentWins_ReplaceAfterRemove()
    {
        // The form lets the user change their mind; only the final intent may reach the wire.
        var request = new UpdateCardiMemberRequest();

        ProfilePhotoEdit.Remove.ApplyTo(request);
        ProfilePhotoEdit.Replace("aGVsbG8=").ApplyTo(request);

        Assert.Equal("aGVsbG8=", request.PhotoBase64);
        Assert.False(request.RemovePhoto);
    }

    [Fact]
    public void TheLastIntentWins_RemoveAfterReplace()
    {
        var request = new UpdateCardiMemberRequest();

        ProfilePhotoEdit.Replace("aGVsbG8=").ApplyTo(request);
        ProfilePhotoEdit.Remove.ApplyTo(request);

        Assert.Null(request.PhotoBase64);
        Assert.True(request.RemovePhoto);
    }

    [Fact]
    public void KeepAfterReplace_ClearsThePayload()
    {
        // "Save without the photo" after a failed preparation must not leak the payload.
        var request = new UpdateCardiMemberRequest();

        ProfilePhotoEdit.Replace("aGVsbG8=").ApplyTo(request);
        ProfilePhotoEdit.Keep.ApplyTo(request);

        Assert.Null(request.PhotoBase64);
        Assert.False(request.RemovePhoto);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Replace_RefusesAnEmptyPayload(string? base64)
    {
        // An empty payload reads as "keep" on the wire — a caller who meant to replace
        // must hear about it here, not silently keep.
        Assert.Throws<ArgumentException>(() => ProfilePhotoEdit.Replace(base64!));
    }

    [Fact]
    public void OnlyKeep_IsKeep()
    {
        Assert.True(ProfilePhotoEdit.Keep.IsKeep);
        Assert.False(ProfilePhotoEdit.Remove.IsKeep);
        Assert.False(ProfilePhotoEdit.Replace("aGVsbG8=").IsKeep);
    }
}
