using System.Text;
using CardiTrack.Application.Exceptions;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using SkiaSharp;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The processor is the only gate between caregiver-uploaded bytes and the photo bucket, so
/// these tests pin its refusals (format, size, dimensions) and its guarantees: every output is
/// a freshly-encoded JPEG with no metadata, judged by content alone — there is no filename or
/// content type in the contract for a disguise to live in.
///
/// EXIF fixtures are assembled here by hand rather than by a metadata library. That is
/// deliberate: Skia neither writes nor exposes EXIF, so a library-based assertion would only be
/// testing the library. Splicing a real APP1 segment in and then scanning the output bytes for
/// it proves the strip at the only level that matters — what actually lands in the bucket.
/// </summary>
public class SkiaProfilePhotoProcessorTests
{
    private static readonly SKColor Fill = new(200, 120, 40);

    private static SkiaProfilePhotoProcessor CreateSut(int? maxUploadBytes = null)
    {
        var options = new MemberPhotoStorageOptions();
        if (maxUploadBytes is { } max)
            options.MaxUploadBytes = max;
        return new SkiaProfilePhotoProcessor(options);
    }

    /// <summary>A solid-colour test image.</summary>
    private static byte[] BuildImage(int width, int height, bool asPng = false)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(Fill);
        }

        return Encode(bitmap, asPng);
    }

    private static byte[] Encode(SKBitmap bitmap, bool asPng)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(
            asPng ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg,
            asPng ? 100 : 95);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] bytes)
    {
        var decoded = SKBitmap.Decode(bytes);
        Assert.NotNull(decoded);
        return decoded;
    }

    private static SKEncodedImageFormat FormatOf(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        return codec.EncodedFormat;
    }

    // ---- EXIF fixture construction -------------------------------------------------------

    /// <summary>Splices an APP1 EXIF segment carrying <paramref name="tiff"/> in directly after the SOI marker.</summary>
    private static byte[] WithExifSegment(byte[] jpeg, byte[] tiff)
    {
        Assert.True(jpeg[0] == 0xFF && jpeg[1] == 0xD8, "fixture is not a JPEG");

        ReadOnlySpan<byte> exifHeader = "Exif\0\0"u8;
        // The segment length covers its own two bytes, the "Exif\0\0" header and the TIFF block.
        var segmentLength = 2 + exifHeader.Length + tiff.Length;

        var result = new List<byte>(jpeg.Length + segmentLength + 2);
        result.AddRange(jpeg[..2]);                       // SOI
        result.AddRange([0xFF, 0xE1]);                    // APP1
        result.Add((byte)(segmentLength >> 8));
        result.Add((byte)(segmentLength & 0xFF));
        result.AddRange(exifHeader);
        result.AddRange(tiff);
        result.AddRange(jpeg[2..]);
        return result.ToArray();
    }

    private static void WriteUInt16(List<byte> buffer, int value)
    {
        buffer.Add((byte)(value & 0xFF));
        buffer.Add((byte)((value >> 8) & 0xFF));
    }

    private static void WriteUInt32(List<byte> buffer, long value)
    {
        buffer.Add((byte)(value & 0xFF));
        buffer.Add((byte)((value >> 8) & 0xFF));
        buffer.Add((byte)((value >> 16) & 0xFF));
        buffer.Add((byte)((value >> 24) & 0xFF));
    }

    /// <summary>An IFD entry whose value fits in the four inline bytes.</summary>
    private static void WriteInlineEntry(List<byte> buffer, int tag, int type, long count, byte[] inlineValue)
    {
        WriteUInt16(buffer, tag);
        WriteUInt16(buffer, type);
        WriteUInt32(buffer, count);
        buffer.AddRange(inlineValue);
        for (var i = inlineValue.Length; i < 4; i++)
            buffer.Add(0);
    }

    /// <summary>An IFD entry whose value lives in the data area, referenced by offset from the TIFF header.</summary>
    private static void WriteOffsetEntry(List<byte> buffer, int tag, int type, long count, long offset)
    {
        WriteUInt16(buffer, tag);
        WriteUInt16(buffer, type);
        WriteUInt32(buffer, count);
        WriteUInt32(buffer, offset);
    }

    /// <summary>Little-endian TIFF header: byte order, magic 42, and the offset of IFD0.</summary>
    private static void WriteTiffHeader(List<byte> buffer)
    {
        buffer.AddRange("II"u8);
        WriteUInt16(buffer, 42);
        WriteUInt32(buffer, 8);
    }

    /// <summary>A minimal EXIF block carrying nothing but the orientation tag.</summary>
    private static byte[] BuildOrientationExif(int orientation)
    {
        var tiff = new List<byte>();
        WriteTiffHeader(tiff);
        WriteUInt16(tiff, 1);                                                  // one IFD0 entry
        WriteInlineEntry(tiff, 0x0112, 3, 1, [(byte)orientation, 0]);          // Orientation, SHORT
        WriteUInt32(tiff, 0);                                                  // no IFD1
        return tiff.ToArray();
    }

    /// <summary>The GPS coordinate rationals the strip exists to drop — a realistic home position.</summary>
    private static readonly int[] LatitudeParts = [51, 1, 30, 1, 12, 1];
    private static readonly int[] LongitudeParts = [0, 1, 7, 1, 39, 1];
    private const string CameraSoftware = "UnitTest Camera 1.0\0";

    /// <summary>
    /// An EXIF block with a Software tag and a full GPS sub-IFD. Offsets are laid out first so
    /// each entry can reference its own data area, exactly as a camera would write it.
    /// </summary>
    private static byte[] BuildGpsExif()
    {
        const long ifd0Offset = 8;
        const long ifd0Size = 2 + (2 * 12) + 4;          // count + two entries + next-IFD pointer
        const long gpsIfdOffset = ifd0Offset + ifd0Size; // 38
        const long gpsIfdSize = 2 + (4 * 12) + 4;        // count + four entries + next-IFD pointer
        const long softwareOffset = gpsIfdOffset + gpsIfdSize;
        var latitudeOffset = softwareOffset + CameraSoftware.Length;
        var longitudeOffset = latitudeOffset + 24;       // three rationals, eight bytes each

        var tiff = new List<byte>();
        WriteTiffHeader(tiff);

        WriteUInt16(tiff, 2);
        WriteOffsetEntry(tiff, 0x0131, 2, CameraSoftware.Length, softwareOffset);  // Software, ASCII
        WriteOffsetEntry(tiff, 0x8825, 4, 1, gpsIfdOffset);                        // GPSInfoIFDPointer, LONG
        WriteUInt32(tiff, 0);

        WriteUInt16(tiff, 4);
        WriteInlineEntry(tiff, 0x0001, 2, 2, "N\0"u8.ToArray());                   // GPSLatitudeRef
        WriteOffsetEntry(tiff, 0x0002, 5, 3, latitudeOffset);                      // GPSLatitude, RATIONAL
        WriteInlineEntry(tiff, 0x0003, 2, 2, "W\0"u8.ToArray());                   // GPSLongitudeRef
        WriteOffsetEntry(tiff, 0x0004, 5, 3, longitudeOffset);                     // GPSLongitude, RATIONAL
        WriteUInt32(tiff, 0);

        tiff.AddRange(Encoding.ASCII.GetBytes(CameraSoftware));
        foreach (var part in LatitudeParts)
            WriteUInt32(tiff, part);
        foreach (var part in LongitudeParts)
            WriteUInt32(tiff, part);

        return tiff.ToArray();
    }

    private static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    // ---- Tests ---------------------------------------------------------------------------

    [Fact]
    public void ValidJpeg_IsReencodedAsJpeg_NeverTheInputBytes()
    {
        var input = BuildImage(640, 480);

        var output = CreateSut().Process(input);

        // JPEG magic, and a genuinely fresh encode — byte-identical output would mean the
        // original file (and whatever rode inside it) reached storage untouched.
        Assert.True(output.Length >= 3 && output[0] == 0xFF && output[1] == 0xD8 && output[2] == 0xFF);
        Assert.False(output.AsSpan().SequenceEqual(input));

        using var reloaded = Decode(output);
        Assert.Equal(640, reloaded.Width);
        Assert.Equal(480, reloaded.Height);
    }

    [Fact]
    public void ValidPng_IsConvertedToJpeg()
    {
        var input = BuildImage(300, 200, asPng: true);

        var output = CreateSut().Process(input);

        Assert.True(output[0] == 0xFF && output[1] == 0xD8);
        Assert.Equal(SKEncodedImageFormat.Jpeg, FormatOf(output));
    }

    [Fact]
    public void ExifIncludingGps_IsStrippedFromOutput()
    {
        var input = WithExifSegment(BuildImage(640, 480), BuildGpsExif());

        // Sanity: the input really carries the EXIF block and the GPS payload, otherwise this
        // test proves nothing. Latitude 51/1 is the first rational of the coordinate.
        Assert.True(Contains(input, "Exif\0\0"u8), "fixture lost its EXIF header");
        Assert.True(Contains(input, Encoding.ASCII.GetBytes(CameraSoftware)), "fixture lost its Software tag");
        var latitudeBytes = new byte[] { 51, 0, 0, 0, 1, 0, 0, 0, 30, 0, 0, 0 };
        Assert.True(Contains(input, latitudeBytes), "fixture lost its GPS coordinates");

        var output = CreateSut().Process(input);

        Assert.False(Contains(output, "Exif\0\0"u8), "output still carries an EXIF header");
        Assert.False(Contains(output, Encoding.ASCII.GetBytes(CameraSoftware)), "output still names the camera");
        Assert.False(Contains(output, latitudeBytes), "output still carries the GPS coordinates");

        // And no APP1 segment at all — the marker is what an EXIF block would have to live in.
        Assert.False(Contains(output, [0xFF, 0xE1]), "output still carries an APP1 segment");
    }

    [Theory]
    // orientation, whether the upright image swaps the source's axes
    [InlineData(1, false)] // TopLeft — already upright
    [InlineData(2, false)] // TopRight — mirrored
    [InlineData(3, false)] // BottomRight — 180°
    [InlineData(4, false)] // BottomLeft — mirrored vertically
    [InlineData(5, true)]  // LeftTop — transposed
    [InlineData(6, true)]  // RightTop — 90° clockwise
    [InlineData(7, true)]  // RightBottom — transverse
    [InlineData(8, true)]  // LeftBottom — 270° clockwise
    public void ExifOrientation_IsAppliedToTheOutputDimensions(int orientation, bool swapsAxes)
    {
        var input = WithExifSegment(BuildImage(400, 200), BuildOrientationExif(orientation));

        var output = CreateSut().Process(input);

        using var reloaded = Decode(output);
        Assert.Equal(swapsAxes ? 200 : 400, reloaded.Width);
        Assert.Equal(swapsAxes ? 400 : 200, reloaded.Height);
    }

    /// <summary>Left half red, right half blue — so a rotation is visible in the pixels, not just the shape.</summary>
    private static byte[] BuildTwoToneImage(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(new SKRect(0, 0, width / 2f, height), paint);
        }

        return Encode(bitmap, asPng: false);
    }

    [Theory]
    // orientation, expected output size, a point that must be red, a point that must be blue.
    // Source is 2048x1000 in every case, so the downscale is always engaged alongside the turn.
    //
    // 6 (90° cw)  upright is 1000x2048, scaled by 1024/2048 -> 500x1024; the source's left edge
    //             becomes the top, so red is the top half.
    // 8 (270° cw) same shape, opposite turn — red is the bottom half.
    // 3 (180°)    no axis swap, so 2048x1000 scales to 1024x500; red crosses to the right half.
    [InlineData(6, 500, 1024, 250, 200, 250, 824)]
    [InlineData(8, 500, 1024, 250, 824, 250, 200)]
    [InlineData(3, 1024, 500, 800, 250, 200, 250)]
    public void RotationAndDownscale_ComposeInOneStep(
        int orientation, int expectedWidth, int expectedHeight,
        int redX, int redY, int blueX, int blueY)
    {
        // The orientation and the scale are multiplied into a single matrix, and neither factor
        // is the identity here. Rotation-only and downscale-only cases both pass with the two
        // composed in the wrong order, or with the target size computed before the axis swap —
        // this is the case that does not.
        var input = WithExifSegment(BuildTwoToneImage(2048, 1000), BuildOrientationExif(orientation));

        var output = CreateSut().Process(input);

        using var reloaded = Decode(output);
        Assert.Equal(expectedWidth, reloaded.Width);
        Assert.Equal(expectedHeight, reloaded.Height);

        var red = reloaded.GetPixel(redX, redY);
        var blue = reloaded.GetPixel(blueX, blueY);
        Assert.True(red.Red > 150 && red.Blue < 100, $"expected red at ({redX},{redY}), got {red}");
        Assert.True(blue.Blue > 150 && blue.Red < 100, $"expected blue at ({blueX},{blueY}), got {blue}");
    }

    [Fact]
    public void ExifOrientation_MovesThePixelsThemselves_NotJustTheDimensions()
    {
        // Left half red, right half blue. Orientation 6 is a 90° clockwise turn, which puts the
        // source's left edge along the destination's top — so red must end up as the top half.
        // Dimensions alone would pass with the rotation applied backwards; pixels will not.
        var input = WithExifSegment(BuildTwoToneImage(40, 20), BuildOrientationExif(6));

        var output = CreateSut().Process(input);

        using var reloaded = Decode(output);
        Assert.Equal(20, reloaded.Width);
        Assert.Equal(40, reloaded.Height);

        // Sample well inside each half, away from JPEG ringing at the boundary.
        var top = reloaded.GetPixel(10, 4);
        var bottom = reloaded.GetPixel(10, 35);
        Assert.True(top.Red > 150 && top.Blue < 100, $"expected red at the top, got {top}");
        Assert.True(bottom.Blue > 150 && bottom.Red < 100, $"expected blue at the bottom, got {bottom}");
    }

    [Fact]
    public void TransparentPng_IsFlattenedOntoWhite_NotBlack()
    {
        // JPEG has no alpha. Skia's premultiplied pixels would composite a transparent PNG onto
        // black, which reads as a broken upload; the processor fills white first.
        using var bitmap = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
        }

        var output = CreateSut().Process(Encode(bitmap, asPng: true));

        using var reloaded = Decode(output);
        var pixel = reloaded.GetPixel(32, 32);
        Assert.True(pixel.Red > 240 && pixel.Green > 240 && pixel.Blue > 240, $"expected white, got {pixel}");
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
        // arbitrary content passes the first sniff and must still die in Skia's decoder.
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

        using var reloaded = Decode(output);
        Assert.Equal(1024, reloaded.Width);
        Assert.Equal(500, reloaded.Height);
    }

    [Theory]
    // Ratios that do not divide evenly, so the scale factor is irrational in float terms.
    [InlineData(2048, 1000)]
    [InlineData(1707, 999)]
    [InlineData(1365, 767)]
    [InlineData(3000, 1237)]
    public void DownscaledImage_ReachesItsOwnEdges(int width, int height)
    {
        // The processor fills white before drawing, so anything that leaves the drawn image
        // short of its bitmap shows up as a white line down the right or bottom edge. Sampling
        // the last row and column keeps that honest as the transform changes — a solid source
        // must still be solid at its edges.
        var input = BuildImage(width, height);

        var output = CreateSut().Process(input);

        using var reloaded = Decode(output);
        var right = reloaded.GetPixel(reloaded.Width - 1, reloaded.Height / 2);
        var bottom = reloaded.GetPixel(reloaded.Width / 2, reloaded.Height - 1);

        Assert.True(right.Red is > 150 and < 250, $"right edge is not the fill colour: {right}");
        Assert.True(bottom.Red is > 150 and < 250, $"bottom edge is not the fill colour: {bottom}");
    }

    [Fact]
    public void SmallerImage_IsNeverUpscaled()
    {
        var input = BuildImage(100, 80);

        var output = CreateSut().Process(input);

        using var reloaded = Decode(output);
        Assert.Equal(100, reloaded.Width);
        Assert.Equal(80, reloaded.Height);
    }
}
