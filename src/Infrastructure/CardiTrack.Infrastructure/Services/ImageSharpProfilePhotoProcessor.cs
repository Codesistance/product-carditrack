using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Settings;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The one place uploaded photo bytes are opened. Content is sniffed twice — magic bytes first,
/// then ImageSharp's own format detection — so only real JPEG/PNG data is ever decoded, whatever
/// extension or content type the client claimed; SVG (script-capable), GIF, WebP and everything
/// else are refused. The output is always a fresh JPEG encode of a decoded pixel grid with every
/// metadata block dropped, which is what actually guarantees EXIF GPS coordinates, serial
/// numbers and embedded thumbnails cannot ride an uploaded file into the bucket — stripping is
/// not a filter over the input bytes, the input bytes are simply never stored.
/// </summary>
public class ImageSharpProfilePhotoProcessor : IProfilePhotoProcessor
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

    private readonly int _maxUploadBytes;

    public ImageSharpProfilePhotoProcessor(MemberPhotoStorageOptions options)
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

        var decoderOptions = new DecoderOptions { MaxFrames = 1 };

        try
        {
            using var source = new MemoryStream(uploadBytes.ToArray(), writable: false);

            // ImageSharp's verdict on the format, independent of the magic-byte sniff: a file
            // wearing a JPEG header over other content fails here, not in the bucket.
            var info = Image.Identify(decoderOptions, source);
            if (info.Metadata.DecodedImageFormat is not (JpegFormat or PngFormat))
                throw new InvalidProfilePhotoException("Photos must be JPEG or PNG images.");

            if (info.Width > MaxSourceEdgePixels || info.Height > MaxSourceEdgePixels)
            {
                throw new InvalidProfilePhotoException(
                    $"Photos can be at most {MaxSourceEdgePixels}×{MaxSourceEdgePixels} pixels.");
            }

            source.Position = 0;
            using var image = Image.Load<Rgba32>(decoderOptions, source);

            // Apply the EXIF orientation to the pixels BEFORE the metadata carrying it is
            // dropped, or every portrait phone photo comes out sideways.
            image.Mutate(x => x.AutoOrient());

            // Longest edge to MaxEdgePixels — but only downward. ResizeMode.Max alone would
            // upscale a smaller photo to fill the box, inventing pixels for no reader's benefit.
            if (image.Width > MaxEdgePixels || image.Height > MaxEdgePixels)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(MaxEdgePixels, MaxEdgePixels),
                }));
            }

            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.IccProfile = null;

            using var output = new MemoryStream();
            image.Save(output, new JpegEncoder { Quality = JpegQuality });
            return output.ToArray();
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            throw new InvalidProfilePhotoException(
                "The photo couldn't be read as a JPEG or PNG image.", ex);
        }
    }

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
