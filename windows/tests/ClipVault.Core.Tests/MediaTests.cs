using ClipVault.Core;
using ClipVault.Core.Tests.TestSupport;

namespace ClipVault.Core.Tests;

// Ported from the #[cfg(test)] module in src-tauri/src/media.rs.
public class MediaTests
{
    [Fact]
    public void PreparesDimensionsAndThumbnail()
    {
        var c = Media.Prepare(PixelFixture.Png(400, 200, 10));
        Assert.Equal((400u, 200u), (c.Width, c.Height));
        Assert.NotEmpty(c.Png);
        Assert.NotEmpty(c.ThumbPng);
        Assert.True(c.ThumbPng.Length < c.Png.Length, "thumbnail should be smaller");
    }

    [Fact]
    public void HashFollowsPixelsNotEncoding()
    {
        var original = PixelFixture.Png(60, 40, 7);
        var once = Media.Prepare(original);
        // Re-encoding the image the way a clipboard round trip would.
        var twice = Media.Prepare(once.Png);
        Assert.Equal(once.Hash, twice.Hash);
    }

    [Fact]
    public void DifferentImagesHashDifferently()
    {
        Assert.NotEqual(
            Media.Prepare(PixelFixture.Png(60, 40, 7)).Hash,
            Media.Prepare(PixelFixture.Png(60, 40, 9)).Hash);
    }

    [Fact]
    public void RgbaRoundTripPreservesSize()
    {
        var c = Media.Prepare(PixelFixture.Png(32, 24, 3));
        var (w, h, rgba) = Media.ToRgba(c.Png);
        Assert.Equal((32u, 24u), (w, h));
        Assert.Equal(32 * 24 * 4, rgba.Length);
    }

    [Fact]
    public void RejectsJunk()
    {
        Assert.ThrowsAny<Exception>(() => Media.Prepare("not an image at all"u8.ToArray()));
    }
}
