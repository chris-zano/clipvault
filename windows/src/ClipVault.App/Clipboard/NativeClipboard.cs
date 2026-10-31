using System.Runtime.InteropServices;

namespace ClipVault.App.Clipboard;

/// <summary>
/// Raw Win32 clipboard plumbing shared by <see cref="ClipboardWatcher"/> and
/// <see cref="ClipboardWriter"/>: opening with retry, moving bytes in and out
/// of global memory, and the handful of format ids/names both sides need.
/// </summary>
internal static class NativeClipboard
{
    public const uint WmClipboardUpdate = 0x031D;

    public const uint CfText = 1;
    public const uint CfHdrop = 15;
    public const uint CfUnicodeText = 13;
    public const uint CfDib = 8;
    public const uint CfDibV5 = 17;

    /// <summary>
    /// Opt-out formats real apps set: password managers, incognito browser
    /// windows, and similar. The Windows analogue of the KDE
    /// `x-kde-passwordManagerHint` check in `watcher.rs`.
    /// </summary>
    public const string CanIncludeInClipboardHistoryFormat = "CanIncludeInClipboardHistory";

    /// <summary>Same opt-out, for Cloud Clipboard's sync-to-other-devices feature.</summary>
    public const string CanUploadToCloudClipboardFormat = "CanUploadToCloudClipboard";

    /// <summary>
    /// A vault secret must never land in Windows' own Win+V history or Cloud
    /// Clipboard either, however briefly it sits on the clipboard - setting
    /// this format (its content does not matter, only its presence) tells
    /// both to leave this clipboard content alone.
    /// </summary>
    public const string ExcludeFromMonitorProcessingFormat = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>Many apps (browsers, Office) offer this directly for alpha-correct images.</summary>
    public const string PngFormat = "PNG";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    public const uint GmemMoveable = 0x0002;

    /// <summary>
    /// <c>OpenClipboard</c> fails with access denied while another process
    /// (or even our own watcher and writer racing each other) has it open.
    /// Windows hands over the whole clipboard on open, unlike X11's
    /// per-request `ConvertSelection`, so there is no timestamp/INCR dance to
    /// replicate here - just this retry, mirroring the "owner refused the
    /// first ask" retry in `watcher.rs`'s <c>fetch</c>.
    /// </summary>
    public static bool TryOpenClipboard(IntPtr hwnd, int attempts = 8, int delayMs = 15)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (OpenClipboard(hwnd))
            {
                return true;
            }
            Thread.Sleep(delayMs);
        }
        return false;
    }

    /// <summary>Copies a global memory block's contents out as a byte array, without taking ownership of it.</summary>
    public static byte[] ReadGlobal(IntPtr hGlobal)
    {
        var ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            return [];
        }
        try
        {
            var size = (int)GlobalSize(hGlobal);
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }
    }

    public static string ReadUnicodeText(IntPtr hGlobal)
    {
        var ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            return string.Empty;
        }
        try
        {
            return Marshal.PtrToStringUni(ptr) ?? string.Empty;
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }
    }

    public static string ReadAnsiText(IntPtr hGlobal)
    {
        var ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            return string.Empty;
        }
        try
        {
            return Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }
    }

    public static int ReadInt32(IntPtr hGlobal)
    {
        var ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            return 0;
        }
        try
        {
            return Marshal.ReadInt32(ptr);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }
    }

    /// <summary>Allocates movable global memory and copies <paramref name="bytes"/> into it, ready for <c>SetClipboardData</c>.</summary>
    public static IntPtr AllocGlobal(ReadOnlySpan<byte> bytes)
    {
        var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (hGlobal == IntPtr.Zero)
        {
            throw new InvalidOperationException("GlobalAlloc failed");
        }
        var ptr = GlobalLock(hGlobal);
        try
        {
            Marshal.Copy(bytes.ToArray(), 0, ptr, bytes.Length);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }
        return hGlobal;
    }

    public static IntPtr AllocGlobalUnicodeText(string text)
    {
        var byteCount = (text.Length + 1) * 2; // UTF-16 plus the null terminator
        var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)byteCount);
        if (hGlobal == IntPtr.Zero)
        {
            throw new InvalidOperationException("GlobalAlloc failed");
        }
        var ptr = GlobalLock(hGlobal);
        try
        {
            Marshal.Copy((text + '\0').ToCharArray(), 0, ptr, text.Length + 1);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }
        return hGlobal;
    }
}
