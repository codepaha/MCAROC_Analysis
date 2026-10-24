using System.Buffers.Binary;
using MCAROC_Analysis.Services.PreLoginReports;
using SkiaSharp;

namespace MCAROC_Analysis.Tests;

/// <summary>Uploaded screenshots are refused above 25 megapixels before OCR. The size is read from the image header (SkiaSharp), so a huge image is refused
/// without its pixels ever being decoded.</summary>
public sealed class ImageDimensionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "image-dimensions-" + Guid.NewGuid().ToString("N"));

    public ImageDimensionsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Encoded(int width, int height, SKEncodedImageFormat format, string name)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.White);
        using var data = bitmap.Encode(format, 90);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void A_screenshot_s_width_and_height_are_read_from_its_header()
    {
        Assert.Equal((320, 200), ImageDimensions.Read(Encoded(320, 200, SKEncodedImageFormat.Png, "shot.png")));
        Assert.Equal((64, 48), ImageDimensions.Read(Encoded(64, 48, SKEncodedImageFormat.Jpeg, "shot.jpg")));
        Assert.False(ImageDimensions.IsMissingOrLargerThan(Path.Combine(_dir, "shot.png"), 25_000_000));
    }

    /// <summary>A PNG whose header says 6000 × 5000 (30 MP): refused from the header alone; nothing that large is built or decoded.</summary>
    [Fact]
    public void An_image_over_the_limit_is_refused_from_its_header()
    {
        var path = Path.Combine(_dir, "huge.png");
        File.WriteAllBytes(path, PngHeader(6000, 5000));

        Assert.Equal((6000, 5000), ImageDimensions.Read(path));
        Assert.True(ImageDimensions.IsMissingOrLargerThan(path, 25_000_000));
    }

    [Fact]
    public void A_file_that_is_not_an_image_or_does_not_exist_is_refused()
    {
        var text = Path.Combine(_dir, "notes.png");
        File.WriteAllText(text, "not an image at all");

        Assert.Null(ImageDimensions.Read(text));
        Assert.True(ImageDimensions.IsMissingOrLargerThan(text, 25_000_000));
        Assert.True(ImageDimensions.IsMissingOrLargerThan(Path.Combine(_dir, "missing.png"), 25_000_000));
    }

    // PNG signature + IHDR (8-bit RGB) + an empty IDAT + IEND, each chunk with its CRC: a valid header for any declared size.
    private static byte[] PngHeader(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2;
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        s.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
