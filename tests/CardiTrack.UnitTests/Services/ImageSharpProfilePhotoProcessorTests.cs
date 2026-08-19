using System.Text;
using CardiTrack.Application.Exceptions;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The processor is the only gate between caregiver-uploaded bytes and the photo bucket, so
/// these tests pin its refusals (format, size, dimensions) and its guarantees: every output is
/// a freshly-encoded JPEG with no metadata, judged by content alone — there is no filename or
/// content type in the contract for a disguise to live in.
/// </summary>
public class ImageSharpProfilePhotoProcessorTests
{
    private static ImageSharpProfilePhotoProcessor CreateSut(int? maxUploadBytes = null)
    {
        var options = new MemberPhotoStorageOptions();
        if (maxUploadBytes is { } max)
            options.MaxUploadBytes = max;
        return new ImageSharpProfilePhotoProcessor(options);
    }

    /// <summary>A solid-colour test image, optionally carrying an EXIF block with a GPS position.</summary>
    private static byte[] BuildImage(int width, int height, bool asPng = false, bool withGpsExif = false)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 120, 40));

        if (withGpsExif)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Software, "UnitTest Camera 1.0");
            // A realistic looking home position — exactly the payload the strip exists to drop.
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(51, 1), new Rational(30, 1), new Rational(12, 1)]);
            exif.SetValue(ExifTag.GPSLongitudeRef, "W");
            exif.SetValue(ExifTag.GPSLongitude, [new Rational(0, 1), new Rational(7, 1), new Rational(39, 1)]);
            image.Metadata.ExifProfile = exif;
        }

        using var stream = new MemoryStream();
        if (asPng)
            image.SaveAsPng(stream);
        else
            image.SaveAsJpeg(stream, new JpegEncoder { Quality = 95 });
        return stream.ToArray();
    }

    private static Image<Rgba32> LoadOutput(byte[] processed) =>
        Image.Load<Rgba32>(processed);

    [Fact]
    public void ValidJpeg_IsReencodedAsJpeg_NeverTheInputBytes()
    {
        var input = BuildImage(640, 480);

        var output = CreateSut().Process(input);

        // JPEG magic, and a genuinely fresh encode — byte-identical output would mean the
        // original file (and whatever rode inside it) reached storage untouched.
        Assert.True(output.Length >= 3 && output[0] == 0xFF && output[1] == 0xD8 && output[2] == 0xFF);
        Assert.False(output.AsSpan().SequenceEqual(input));

        using var reloaded = LoadOutput(output);
        Assert.Equal(640, reloaded.Width);
        Assert.Equal(480, reloaded.Height);
    }

    [Fact]
    public void ValidPng_IsConvertedToJpeg()
    {
        var input = BuildImage(300, 200, asPng: true);

        var output = CreateSut().Process(input);

        Assert.True(output[0] == 0xFF && output[1] == 0xD8);
        using var stream = new MemoryStream(output);
        var info = Image.Identify(stream);
        Assert.IsType<JpegFormat>(info.Metadata.DecodedImageFormat);
    }

    [Fact]
    public void ExifIncludingGps_IsStrippedFromOutput()
    {
        var input = BuildImage(640, 480, withGpsExif: true);

        // Sanity: the input really carries the GPS tag, otherwise this test proves nothing.
        using (var original = Image.Load<Rgba32>(input))
        {
            Assert.NotNull(original.Metadata.ExifProfile);
            Assert.True(original.Metadata.ExifProfile!.TryGetValue(ExifTag.GPSLatitude, out _));
        }

        var output = CreateSut().Process(input);

        using var reloaded = LoadOutput(output);
        Assert.Null(reloaded.Metadata.ExifProfile);
        Assert.Null(reloaded.Metadata.XmpProfile);
        Assert.Null(reloaded.Metadata.IptcProfile);
    }

    [Fact]
    public void OversizedByteInput_IsRejectedBeforeDecoding()
    {
        // A tight cap via options, so the guard under test is the byte-length check itself —
        // the "image" is pure garbage, and must be refused for size before anything decodes it.
        var tooBig = new byte[64];

        var ex = Assert.Throws<InvalidProfilePhotoException>(
            () => CreateSut(maxUploadBytes: 63).Process(tooBig));

        Assert.Contains("5 MB", ex.Message);
    }

    [Fact]
    public void OversizedDimensions_AreRejected()
    {
        // 9000×100 stays tiny as bytes but breaches the 8192px source-edge guard.
        var input = BuildImage(9000, 100);

        Assert.Throws<InvalidProfilePhotoException>(() => CreateSut().Process(input));
    }

    [Fact]
    public void NonImageBytes_AreRejected()
    {
        var input = Encoding.UTF8.GetBytes("definitely not an image");

        Assert.Throws<InvalidProfilePhotoException>(() => CreateSut().Process(input));
    }

    [Fact]
    public void EmptyInput_IsRejected()
    {
        Assert.Throws<InvalidProfilePhotoException>(() => CreateSut().Process(Array.Empty<byte>()));
    }

    [Fact]
    public void GifBytes_AreRejected()
    {
        // A structurally valid 1×1 GIF — a real image, just not one of the two allowed formats.
        var input = new byte[]
        {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
            0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00,
            0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B,
        };

        Assert.Throws<InvalidProfilePhotoException>(() => CreateSut().Process(input));
    }

    [Fact]
    public void SvgBytes_AreRejected()
    {
        var input = Encoding.UTF8.GetBytes(
            """<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""");

        Assert.Throws<InvalidProfilePhotoException>(() => CreateSut().Process(input));
    }

    [Fact]
    public void JpegMagicOverNonJpegContent_IsRejected()
    {
        // Content sniffing only — and sniffing twice: a file wearing JPEG magic bytes over
        // arbitrary content passes the first sniff and must still die in ImageSharp's decoder.
        // (There is no filename in the contract, so an extension-based disguise cannot even
        // be expressed; this is the byte-level equivalent.)
        var disguised = new byte[] { 0xFF, 0xD8, 0xFF }
            .Concat(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"))
            .ToArray();

        Assert.Throws<InvalidProfilePhotoException>(() => CreateSut().Process(disguised));
    }

    [Fact]
    public void LongestEdge_IsDownscaledTo1024()
    {
        var input = BuildImage(2048, 1000);

        var output = CreateSut().Process(input);

        using var reloaded = LoadOutput(output);
        Assert.Equal(1024, reloaded.Width);
        Assert.Equal(500, reloaded.Height);
    }

    [Fact]
    public void SmallerImage_IsNeverUpscaled()
    {
        var input = BuildImage(100, 80);

        var output = CreateSut().Process(input);

        using var reloaded = LoadOutput(output);
        Assert.Equal(100, reloaded.Width);
        Assert.Equal(80, reloaded.Height);
    }
}
