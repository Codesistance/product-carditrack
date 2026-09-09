using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Settings;
using SkiaSharp;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The one place uploaded photo bytes are opened. Content is sniffed twice — magic bytes first,
/// then Skia's own codec detection — so only real JPEG/PNG data is ever decoded, whatever
/// extension or content type the client claimed; SVG (script-capable), GIF, WebP and everything
/// else are refused. The output is always a fresh JPEG encode of a decoded pixel grid, which is
/// what actually guarantees EXIF GPS coordinates, serial numbers and embedded thumbnails cannot
/// ride an uploaded file into the bucket — stripping is not a filter over the input bytes, the
/// input bytes are simply never stored. Skia's JPEG encoder writes pixels and nothing else, so
/// there is no metadata block to clear: none is carried across the decode in the first place.
/// </summary>
public class SkiaProfilePhotoProcessor : IProfilePhotoProcessor
{
    /// <summary>Longest output edge. Avatars render at most a few hundred px; 1024 keeps retina headroom.</summary>
    private const int MaxEdgePixels = 1024;

    /// <summary>
    /// Largest source dimensions the decoder will accept. A dimension bomb (a tiny file
    /// declaring a 60k×60k canvas) allocates at decode, not at download — so the guard reads
    /// the header first and refuses before any pixel buffer exists.
    /// </summary>
    private const int MaxSourceEdgePixels = 8192;

    private const int JpegQuality = 85;

    /// <summary>
    /// JPEG has no alpha channel. Skia stores decoded pixels premultiplied, so a transparent
    /// PNG left to its own devices lands on black; avatars composite onto white instead.
    /// </summary>
    private static readonly SKColor FlattenBackground = SKColors.White;

    private readonly int _maxUploadBytes;

    public SkiaProfilePhotoProcessor(MemberPhotoStorageOptions options)
    {
        _maxUploadBytes = options.MaxUploadBytes;
    }

    public byte[] Process(ReadOnlyMemory<byte> uploadBytes)
    {
        if (uploadBytes.IsEmpty)
            throw new InvalidProfilePhotoException("The photo is empty.");

        // By byte length, before any decode — the whole point is to refuse work, not to start it.
        if (uploadBytes.Length > _maxUploadBytes)
            throw new InvalidProfilePhotoException("Photos can be at most 5 MB.");

        if (!LooksLikeJpegOrPng(uploadBytes.Span))
            throw new InvalidProfilePhotoException("Photos must be JPEG or PNG images.");

        using var data = SKData.CreateCopy(uploadBytes.ToArray());

        // Header-only: this parses the stream's metadata and allocates no pixel buffer, which is
        // what lets the dimension guard below run before a decompression bomb can cost anything.
        using var codec = SKCodec.Create(data);
        if (codec is null)
            throw new InvalidProfilePhotoException("The photo couldn't be read as a JPEG or PNG image.");

        // Skia's verdict on the format, independent of the magic-byte sniff: a file wearing a
        // JPEG header over other content fails here, not in the bucket.
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png))
            throw new InvalidProfilePhotoException("Photos must be JPEG or PNG images.");

        var sourceInfo = codec.Info;
        if (sourceInfo.Width > MaxSourceEdgePixels || sourceInfo.Height > MaxSourceEdgePixels)
        {
            throw new InvalidProfilePhotoException(
                $"Photos can be at most {MaxSourceEdgePixels}×{MaxSourceEdgePixels} pixels.");
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
            throw new InvalidProfilePhotoException("The photo couldn't be read as a JPEG or PNG image.");

        using var rendered = RenderUpright(decoded, codec.EncodedOrigin);
        using var image = SKImage.FromBitmap(rendered);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
            ?? throw new InvalidProfilePhotoException("The photo couldn't be re-encoded as a JPEG.");

        return encoded.ToArray();
    }

    /// <summary>
    /// Applies the EXIF orientation and the downscale in a single draw. Both are expressed as one
    /// matrix on purpose: composing them resamples the pixels once rather than twice, and it
    /// removes any chance of rotating by the pre-scale dimensions or vice versa. The orientation
    /// is consumed here and never written to the output, which is the point — the rotation has to
    /// reach the pixels before the tag describing it disappears, or every portrait phone photo
    /// comes out sideways.
    /// </summary>
    private static SKBitmap RenderUpright(SKBitmap source, SKEncodedOrigin origin)
    {
        // Origins 5-8 are the transposed quarter-turns: they exchange the axes, so the upright
        // image is the source with width and height swapped.
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var uprightWidth = swapsAxes ? source.Height : source.Width;
        var uprightHeight = swapsAxes ? source.Width : source.Height;

        // Longest edge to MaxEdgePixels — but only downward. Scaling up would invent pixels for
        // no reader's benefit, so a photo already inside the box keeps its own dimensions.
        var scale = Math.Min(
            1.0,
            Math.Min((double)MaxEdgePixels / uprightWidth, (double)MaxEdgePixels / uprightHeight));
        var targetWidth = Math.Max(1, (int)Math.Round(uprightWidth * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(uprightHeight * scale));

        var info = new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var destination = new SKBitmap(info);

        try
        {
            using var canvas = new SKCanvas(destination);
            canvas.Clear(FlattenBackground);
            canvas.SetMatrix(SKMatrix.Concat(
                SKMatrix.CreateScale((float)scale, (float)scale),
                OrientationMatrix(origin, source.Width, source.Height)));

            using var sourceImage = SKImage.FromBitmap(source);
            canvas.DrawImage(
                sourceImage,
                new SKPoint(0, 0),
                new SKSamplingOptions(SKCubicResampler.Mitchell));

            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Maps source pixel coordinates onto their upright position for each of the eight EXIF
    /// orientations, in terms of the source's own <paramref name="width"/> and
    /// <paramref name="height"/>. <see cref="SKEncodedOrigin"/> follows the EXIF numbering, so
    /// TopLeft is the already-upright case and needs no transform.
    /// </summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        // scaleX, skewX, transX, skewY, scaleY, transY, persp0, persp1, persp2
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    /// <summary>
    /// Magic-byte sniff — content, never the claimed extension or content type. JPEG opens
    /// FF D8 FF; PNG opens 89 50 4E 47 0D 0A 1A 0A.
    /// </summary>
    private static bool LooksLikeJpegOrPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return true;

        ReadOnlySpan<byte> pngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return bytes.Length >= pngMagic.Length && bytes[..pngMagic.Length].SequenceEqual(pngMagic);
    }
}
