using ClipVault.Core;

namespace ClipVault.Core.Tests.TestSupport;

/// <summary>
/// Builds a Clip.ImageClip from a real PNG fixture via Media.Prepare, mirroring
/// store.rs's `image_clip` test helper (which calls `crate::media::prepare`).
/// </summary>
internal static class TestImages
{
    public static Clip.ImageClip MakeClip(uint width, uint height, byte shade)
    {
        var png = PixelFixture.Png((int)width, (int)height, shade, blue: 64);
        return new Clip.ImageClip(Media.Prepare(png));
    }
}
