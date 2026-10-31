using SkiaSharp;

namespace ClipVault.Core.Tests.TestSupport;

/// <summary>
/// Builds a real PNG fixture, the same way the Rust tests do with
/// `image::RgbaImage::from_fn(w, h, |x, _| Rgba([shade, x % 256, blue, 255]))`.
/// </summary>
internal static class PixelFixture
{
    public static byte[] Png(int width, int height, byte shade, byte blue = 128)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(shade, (byte)(x % 256), blue, 255));
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
