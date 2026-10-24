using SkiaSharp;

namespace MCAROC_Analysis.Services.PreLoginReports;

/// <summary>An uploaded screenshot's size, read from its header without decoding the pixels: enough to refuse one too large to OCR. Uses SkiaSharp,
/// already in the build for page rendering (PDFtoImage); ImageSharp was used only for this and its fixed releases need a commercial licence key.</summary>
public static class ImageDimensions
{
    /// <summary>Width and height, or null when the file is not an image SkiaSharp recognises (or cannot be read).</summary>
    public static (int Width, int Height)? Read(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            return codec is null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether the image has more than <paramref name="maxPixels"/> pixels, or isn't a readable image at all (both are refused).</summary>
    public static bool IsMissingOrLargerThan(string path, long maxPixels) =>
        Read(path) is not { } size || (long)size.Width * size.Height > maxPixels;
}
