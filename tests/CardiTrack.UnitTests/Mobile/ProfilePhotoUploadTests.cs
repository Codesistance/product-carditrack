using CardiTrack.Application.Services;
using CardiTrack.Mobile.Core.Media;

namespace CardiTrack.UnitTests.Mobile;

public sealed class ProfilePhotoUploadTests
{
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02];
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01];

    [Fact]
    public async Task SendsTheTranscodedJpeg_WhenThePlatformCanDecode()
    {
        byte[] transcoded = [1, 2, 3, 4];
        var result = await ProfilePhotoUpload.PrepareAsync(
            JpegHeader, new StubTranscoder(transcoded));

        Assert.True(result.Succeeded);
        Assert.Equal(Convert.ToBase64String(transcoded), result.Base64);
    }

    [Fact]
    public async Task AsksTheTranscoderForThePolicyEdge()
    {
        var transcoder = new StubTranscoder([1]);

        await ProfilePhotoUpload.PrepareAsync(JpegHeader, transcoder);

        Assert.Equal(ProfilePhotoUpload.MaxEdge, transcoder.RequestedMaxEdge);
        Assert.Equal(1280, ProfilePhotoUpload.MaxEdge);
    }

    [Fact]
    public async Task ThePayloadRoundTripsThroughTheServersOwnDecoder()
    {
        // The other half of the contract: what the client encodes, the exact decoder the
        // API's validators and service share must read back to the same bytes.
        byte[] transcoded = [9, 8, 7, 6, 5];
        var result = await ProfilePhotoUpload.PrepareAsync(JpegHeader, new StubTranscoder(transcoded));

        Assert.True(ProfilePhotoBase64.TryDecode(result.Base64, out var decoded));
        Assert.Equal(transcoded, decoded);
    }

    [Fact]
    public async Task FallsBackToTheRawBytes_WhenDecodingFails_ButTheFileIsAJpegUnderTheCap()
    {
        var result = await ProfilePhotoUpload.PrepareAsync(JpegHeader, new StubTranscoder(null));

        Assert.True(result.Succeeded);
        Assert.Equal(Convert.ToBase64String(JpegHeader), result.Base64);
    }

    [Fact]
    public async Task FallsBackToTheRawBytes_WhenDecodingFails_ButTheFileIsAPngUnderTheCap()
    {
        var result = await ProfilePhotoUpload.PrepareAsync(PngHeader, new StubTranscoder(null));

        Assert.True(result.Succeeded);
        Assert.Equal(Convert.ToBase64String(PngHeader), result.Base64);
    }

    [Fact]
    public async Task RefusesAnUndecodableJpeg_OverTheServersSizeCap()
    {
        var oversized = new byte[ProfilePhotoBase64.MaxDecodedBytes + 1];
        JpegHeader.CopyTo(oversized, 0);

        var result = await ProfilePhotoUpload.PrepareAsync(oversized, new StubTranscoder(null));

        Assert.False(result.Succeeded);
        Assert.Contains("5 MB", result.Error);
    }

    [Fact]
    public async Task SendsAJpegExactlyAtTheCap_TheCapIsInclusive_LikeTheServers()
    {
        var atCap = new byte[ProfilePhotoBase64.MaxDecodedBytes];
        JpegHeader.CopyTo(atCap, 0);

        var result = await ProfilePhotoUpload.PrepareAsync(atCap, new StubTranscoder(null));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RefusesBytesThatAreNeitherJpegNorPng_WhenDecodingFails()
    {
        byte[] notAnImage = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61]; // GIF89a

        var result = await ProfilePhotoUpload.PrepareAsync(notAnImage, new StubTranscoder(null));

        Assert.False(result.Succeeded);
        Assert.Contains("JPEG or PNG", result.Error);
    }

    [Fact]
    public async Task ATranscoderCrash_IsTheSameAnswerAsCannotDecode()
    {
        var result = await ProfilePhotoUpload.PrepareAsync(JpegHeader, new ThrowingTranscoder());

        Assert.True(result.Succeeded);
        Assert.Equal(Convert.ToBase64String(JpegHeader), result.Base64);
    }

    [Fact]
    public async Task AnOversizedTranscoderResult_FallsBackRatherThanShippingIt()
    {
        // Cannot happen for a 1280 px JPEG in practice, but the policy still refuses to
        // ship a payload the server's validators would bounce.
        var oversized = new byte[ProfilePhotoBase64.MaxDecodedBytes + 1];

        var result = await ProfilePhotoUpload.PrepareAsync(JpegHeader, new StubTranscoder(oversized));

        Assert.True(result.Succeeded);
        Assert.Equal(Convert.ToBase64String(JpegHeader), result.Base64);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E })]
    public void TruncatedHeadersAreNotImages(byte[] bytes)
    {
        Assert.False(ProfilePhotoUpload.LooksLikeJpegOrPng(bytes));
    }

    private sealed class StubTranscoder(byte[]? answer) : IProfilePhotoTranscoder
    {
        public int? RequestedMaxEdge { get; private set; }

        public Task<byte[]?> TryTranscodeAsync(byte[] source, int maxEdge, CancellationToken ct = default)
        {
            RequestedMaxEdge = maxEdge;
            return Task.FromResult(answer);
        }
    }

    private sealed class ThrowingTranscoder : IProfilePhotoTranscoder
    {
        public Task<byte[]?> TryTranscodeAsync(byte[] source, int maxEdge, CancellationToken ct = default) =>
            throw new InvalidOperationException("Native codec fell over");
    }
}
