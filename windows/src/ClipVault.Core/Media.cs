using System.Buffers.Binary;
using System.Security.Cryptography;
using SkiaSharp;

namespace ClipVault.Core;

/// <summary>Decoding, hashing and thumbnailing for image clips. Ported from `media.rs`.</summary>
public static class Media
{
    /// <summary>Longest edge of the thumbnail shown in the history list.</summary>
    private const int ThumbMax = 160;

    /// <summary>
    /// Identity is the decoded pixels, not the encoded file.
    ///
    /// Copying an image back out of the history re-encodes it through a
    /// different PNG writer, so the bytes change while the picture does not.
    /// Hashing pixels keeps dedupe and echo-suppression working across that
    /// round trip.
    /// </summary>
    private static string PixelHash(uint width, uint height, ReadOnlySpan<byte> rgba)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], height);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(header);
        sha.AppendData(rgba);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    public static ImagePayload Prepare(byte[] bytes)
    {
        using var bitmap = DecodeToRgba(bytes)
            ?? throw new InvalidDataException("unrecognised image data");

        var width = (uint)bitmap.Width;
        var height = (uint)bitmap.Height;
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("image has no pixels");
        }

        var hash = PixelHash(width, height, bitmap.GetPixelSpan());

        // Normalise to PNG so the store holds one format whatever was offered.
        var png = EncodePng(bitmap);

        using var thumb = MakeThumbnail(bitmap);
        var thumbPng = EncodePng(thumb);

        return new ImagePayload(png, thumbPng, width, height, hash);
    }

    /// <summary>Decodes a stored PNG back to the raw RGBA that the clipboard wants.</summary>
    public static (uint Width, uint Height, byte[] Rgba) ToRgba(byte[] png)
    {
        using var bitmap = DecodeToRgba(png)
            ?? throw new InvalidDataException("stored image is unreadable");
        return ((uint)bitmap.Width, (uint)bitmap.Height, bitmap.Bytes);
    }

    /// <summary>
    /// Decodes into straight (non-premultiplied) RGBA8888, matching the
    /// `image` crate's `to_rgba8()`, so pixel bytes are directly comparable
    /// and the clipboard (which also wants straight alpha) can use them as-is.
    /// </summary>
    private static SKBitmap? DecodeToRgba(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        if (codec is null)
        {
            return null;
        }

        var info = codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            return null;
        }
        return bitmap;
    }

    private static SKBitmap MakeThumbnail(SKBitmap source)
    {
        // Mirrors `DynamicImage::resize`: scale to fit within a THUMB_MAX
        // square, keeping the aspect ratio, without cropping.
        var scale = Math.Min((double)ThumbMax / source.Width, (double)ThumbMax / source.Height);
        var newWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var newHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        var info = new SKImageInfo(newWidth, newHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        return source.Resize(info, sampling)
            ?? throw new InvalidOperationException("failed to build the thumbnail");
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static string HumanSize(long bytes) => Hashing.HumanSize(bytes);
}
