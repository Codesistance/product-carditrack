using CardiTrack.Mobile.Core.Media;
#if WINDOWS
using Microsoft.Maui.Graphics.Win2D;
#else
using Microsoft.Maui.Graphics.Platform;
#endif
using IImage = Microsoft.Maui.Graphics.IImage;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// <see cref="IProfilePhotoTranscoder"/> on the platform's own codec via Maui.Graphics —
/// no extra imaging dependency in the app. Shrinks to the policy's longest-edge cap
/// without ever upscaling, and re-encodes as JPEG, which also drops the original's
/// metadata block; the server strips EXIF again regardless and is the authority.
/// </summary>
public sealed class MauiProfilePhotoTranscoder : IProfilePhotoTranscoder
{
    /// <summary>Camera-app territory: visually clean for a portrait, far smaller than lossless.</summary>
    private const float JpegQuality = 0.85f;

    public Task<byte[]?> TryTranscodeAsync(byte[] source, int maxEdge, CancellationToken ct = default) =>
        // Off the UI thread: decoding a 12-megapixel camera frame is real work, and this
        // runs at the moment the user tapped Continue/Save.
        Task.Run(() =>
        {
            try
            {
                using var input = new MemoryStream(source);
#if WINDOWS
                using var image = new W2DImageLoadingService().FromStream(input);
#else
                using var image = PlatformImage.FromStream(input);
#endif
                // Downsize only when actually larger — never upscale a small photo into
                // a blurry big one just to hit a ceiling.
                IImage? resized = image.Width > maxEdge || image.Height > maxEdge
                    ? image.Downsize(maxEdge, disposeOriginal: false)
                    : null;
                try
                {
                    using var output = new MemoryStream();
                    (resized ?? image).Save(output, ImageFormat.Jpeg, JpegQuality);
                    return output.ToArray();
                }
                finally
                {
                    resized?.Dispose();
                }
            }
            catch (Exception)
            {
                // "This platform can't decode these bytes" is the contract's null answer,
                // whatever native exception type carried the news.
                return (byte[]?)null;
            }
        }, ct);
}
