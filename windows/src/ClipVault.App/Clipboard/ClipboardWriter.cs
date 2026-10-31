using ClipVault.Core;

namespace ClipVault.App.Clipboard;

/// <summary>
/// Writes to the clipboard on the app's own behalf - copying a history
/// entry back out, or a vault secret - and tells the <see cref="Suppressor"/>
/// to ignore the echo this causes. Ported from the <c>put_on_clipboard</c> /
/// <c>put_image_on_clipboard</c> helpers in <c>commands.rs</c>.
/// </summary>
internal sealed class ClipboardWriter(Suppressor suppress)
{
    private static readonly uint PngFormat = NativeClipboard.RegisterClipboardFormat(NativeClipboard.PngFormat);
    private static readonly uint CanIncludeInHistoryFormat = NativeClipboard.RegisterClipboardFormat(NativeClipboard.CanIncludeInClipboardHistoryFormat);
    private static readonly uint CanUploadToCloudFormat = NativeClipboard.RegisterClipboardFormat(NativeClipboard.CanUploadToCloudClipboardFormat);
    private static readonly uint ExcludeFromMonitorFormat = NativeClipboard.RegisterClipboardFormat(NativeClipboard.ExcludeFromMonitorProcessingFormat);

    /// <summary>Copies text from history. Suppressed by content, so a real re-copy of the same text still records.</summary>
    public void SetText(string text)
    {
        suppress.Expect(text);
        WriteWithRetry(() => NativeClipboard.SetClipboardData(
            NativeClipboard.CfUnicodeText, NativeClipboard.AllocGlobalUnicodeText(text)));
    }

    /// <summary>
    /// Copies a history image back out. The hash is computed the same way
    /// <c>Media.Prepare</c> hashes it going in, so it still matches after the
    /// pixel data round-trips through the clipboard.
    /// </summary>
    public void SetImagePng(byte[] png)
    {
        var hash = Media.Prepare(png).Hash;
        suppress.ExpectHash(hash);

        var (width, height, rgba) = Media.ToRgba(png);
        WriteWithRetry(() =>
        {
            NativeClipboard.SetClipboardData(NativeClipboard.CfDib, BuildTopDownBgra32Dib(width, height, rgba));
            // Offered alongside CF_DIB (which has no real alpha channel) so
            // an app that reads "PNG" gets the original back losslessly.
            NativeClipboard.SetClipboardData(PngFormat, NativeClipboard.AllocGlobal(png));
        });
    }

    /// <summary>
    /// Copies a vault secret. Unconditional suppression keeps it out of our
    /// own plaintext history table, and the exclusion formats below keep it
    /// out of Windows' own Win+V history and Cloud Clipboard sync too -
    /// neither of which the Linux build has to worry about, since X11 has no
    /// equivalent of either.
    /// </summary>
    public void SetSecretText(string secret)
    {
        suppress.ExpectAny();
        WriteWithRetry(() =>
        {
            NativeClipboard.SetClipboardData(NativeClipboard.CfUnicodeText, NativeClipboard.AllocGlobalUnicodeText(secret));
            NativeClipboard.SetClipboardData(ExcludeFromMonitorFormat, NativeClipboard.AllocGlobal(stackalloc byte[1]));
            NativeClipboard.SetClipboardData(CanIncludeInHistoryFormat, NativeClipboard.AllocGlobal(stackalloc byte[4]));
            NativeClipboard.SetClipboardData(CanUploadToCloudFormat, NativeClipboard.AllocGlobal(stackalloc byte[4]));
        });
    }

    private static void WriteWithRetry(Action setFormats)
    {
        if (!NativeClipboard.TryOpenClipboard(IntPtr.Zero))
        {
            throw new InvalidOperationException("could not open the clipboard to write to it");
        }
        try
        {
            NativeClipboard.EmptyClipboard();
            setFormats();
        }
        finally
        {
            NativeClipboard.CloseClipboard();
        }
    }

    /// <summary>
    /// A 32-bit BI_RGB device-independent bitmap, top-down (negative height)
    /// so the row order matches our already-top-down RGBA buffer and none
    /// need flipping. CF_DIB's 4th byte per pixel is padding, not alpha - callers
    /// that want the real alpha channel should read the "PNG" format instead.
    /// </summary>
    private static IntPtr BuildTopDownBgra32Dib(uint width, uint height, byte[] rgba)
    {
        const int headerSize = 40;
        var pixelBytes = rgba.Length;
        var dib = new byte[headerSize + pixelBytes];

        BitConverter.GetBytes(headerSize).CopyTo(dib, 0);
        BitConverter.GetBytes((int)width).CopyTo(dib, 4);
        BitConverter.GetBytes(-(int)height).CopyTo(dib, 8); // negative: top-down
        BitConverter.GetBytes((short)1).CopyTo(dib, 12); // biPlanes
        BitConverter.GetBytes((short)32).CopyTo(dib, 14); // biBitCount
        // biCompression (16, BI_RGB = 0), biSizeImage (20), the two
        // pels-per-meter fields (24, 28) and biClrUsed/biClrImportant
        // (32, 36) all stay zero.

        for (var i = 0; i < pixelBytes; i += 4)
        {
            // RGBA -> BGRA: Windows DIBs store blue first.
            dib[headerSize + i] = rgba[i + 2];
            dib[headerSize + i + 1] = rgba[i + 1];
            dib[headerSize + i + 2] = rgba[i];
            dib[headerSize + i + 3] = rgba[i + 3];
        }

        return NativeClipboard.AllocGlobal(dib);
    }
}
