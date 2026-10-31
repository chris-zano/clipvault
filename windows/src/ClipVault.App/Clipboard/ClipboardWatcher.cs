using System.Text;
using System.Windows.Interop;

namespace ClipVault.App.Clipboard;

/// <summary>
/// Watches the Windows clipboard for changes. Deliberately does not touch
/// the keyboard, for the same reason `watcher.rs` does not: grabbing Ctrl+C
/// would steal the keystroke from the focused application and would still
/// miss every copy that is not that shortcut (context menus, app buttons,
/// scripts). <c>WM_CLIPBOARDUPDATE</c> fires for all of them.
///
/// Where `watcher.rs` has to fetch a server timestamp and handle an INCR
/// handshake for a large X11 selection, Windows hands over the whole
/// clipboard on <c>OpenClipboard</c>, so neither has an equivalent here -
/// the one thing that does carry over is retrying the open.
/// </summary>
internal sealed class ClipboardWatcher : IDisposable
{
    /// <summary>Anything larger is almost certainly not something you want in a history list.</summary>
    private const int MaxTextBytes = 2 * 1024 * 1024;

    /// <summary>Generous enough for a 4K screenshot.</summary>
    private const int MaxImageBytes = 32 * 1024 * 1024;

    private readonly HwndSource _source;
    private readonly uint _canIncludeInHistoryFormat;
    private readonly uint _pngFormat;

    /// <summary>Raised with whatever is worth keeping; never for something skipped.</summary>
    public event Action<Captured>? CaptureFound;

    public ClipboardWatcher()
    {
        var parameters = new HwndSourceParameters("ClipVaultClipboardWatcherWindow")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        _canIncludeInHistoryFormat = NativeClipboard.RegisterClipboardFormat(NativeClipboard.CanIncludeInClipboardHistoryFormat);
        _pngFormat = NativeClipboard.RegisterClipboardFormat(NativeClipboard.PngFormat);

        if (!NativeClipboard.AddClipboardFormatListener(_source.Handle))
        {
            throw new InvalidOperationException("could not register for clipboard change notifications");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeClipboard.WmClipboardUpdate)
        {
            TryCapture();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Returns whatever is worth keeping, or does nothing when the clipboard
    /// holds something skipped: an unsupported format, an oversized payload,
    /// or a copy an app has opted out of history (e.g. a password manager).
    /// </summary>
    private void TryCapture()
    {
        if (!NativeClipboard.TryOpenClipboard(_source.Handle))
        {
            return;
        }
        try
        {
            // The Windows analogue of the KDE `x-kde-passwordManagerHint`
            // check: honour an app's own opt-out before looking at anything else.
            if (NativeClipboard.IsClipboardFormatAvailable(_canIncludeInHistoryFormat))
            {
                var allowed = NativeClipboard.ReadInt32(NativeClipboard.GetClipboardData(_canIncludeInHistoryFormat));
                if (allowed == 0)
                {
                    return;
                }
            }

            // Text wins when both are offered: copying a file in a file
            // manager or rich content in a browser offers an image alongside
            // the text, and the text is what you actually wanted.
            if (TryReadText(out var text))
            {
                if (!string.IsNullOrWhiteSpace(text) && Encoding.UTF8.GetByteCount(text) <= MaxTextBytes)
                {
                    CaptureFound?.Invoke(new Captured.Text(text));
                }
                return;
            }

            if (TryReadImage(out var bytes))
            {
                if (bytes.Length > 0 && bytes.Length <= MaxImageBytes)
                {
                    CaptureFound?.Invoke(new Captured.Image(bytes));
                }
                return;
            }

            // CF_HDROP (a file list) or some app-private format: nothing to record.
        }
        finally
        {
            NativeClipboard.CloseClipboard();
        }
    }

    private static bool TryReadText(out string text)
    {
        if (NativeClipboard.IsClipboardFormatAvailable(NativeClipboard.CfUnicodeText))
        {
            text = NativeClipboard.ReadUnicodeText(NativeClipboard.GetClipboardData(NativeClipboard.CfUnicodeText));
            return true;
        }
        if (NativeClipboard.IsClipboardFormatAvailable(NativeClipboard.CfText))
        {
            text = NativeClipboard.ReadAnsiText(NativeClipboard.GetClipboardData(NativeClipboard.CfText));
            return true;
        }
        text = string.Empty;
        return false;
    }

    private bool TryReadImage(out byte[] encodedBytes)
    {
        if (NativeClipboard.IsClipboardFormatAvailable(_pngFormat))
        {
            encodedBytes = NativeClipboard.ReadGlobal(NativeClipboard.GetClipboardData(_pngFormat));
            return true;
        }
        if (NativeClipboard.IsClipboardFormatAvailable(NativeClipboard.CfDibV5))
        {
            encodedBytes = BmpFromDib(NativeClipboard.ReadGlobal(NativeClipboard.GetClipboardData(NativeClipboard.CfDibV5)));
            return true;
        }
        if (NativeClipboard.IsClipboardFormatAvailable(NativeClipboard.CfDib))
        {
            encodedBytes = BmpFromDib(NativeClipboard.ReadGlobal(NativeClipboard.GetClipboardData(NativeClipboard.CfDib)));
            return true;
        }
        encodedBytes = [];
        return false;
    }

    /// <summary>
    /// CF_DIB/CF_DIBV5 hand over a BITMAPINFOHEADER (or BITMAPV5HEADER, which
    /// shares the same first 40 bytes) plus an optional colour table plus
    /// pixel data - everything a .bmp file has except its 14-byte file
    /// header. Prefixing that header turns it into a file SkiaSharp can decode.
    /// </summary>
    private static byte[] BmpFromDib(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return [];
        }

        var headerSize = BitConverter.ToInt32(dib, 0);
        var bitCount = BitConverter.ToInt16(dib, 14);
        var colorsUsed = BitConverter.ToInt32(dib, 32);
        var paletteEntries = bitCount <= 8 ? (colorsUsed == 0 ? 1 << bitCount : colorsUsed) : 0;
        var pixelDataOffset = 14 + headerSize + paletteEntries * 4;

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2); // bfSize
        // bytes 6..9 (bfReserved1/2) stay zero
        BitConverter.GetBytes(pixelDataOffset).CopyTo(bmp, 10); // bfOffBits
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    public void Dispose()
    {
        NativeClipboard.RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
