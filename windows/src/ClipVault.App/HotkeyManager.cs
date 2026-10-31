using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClipVault.App;

/// <summary>
/// Registers a global hotkey - Win+Alt+V by default - via
/// <c>RegisterHotKey</c>, using a message-only window so pumping WM_HOTKEY
/// needs no visible window. See PLAN.md's hotkey decision for why Win+V and
/// Win+Shift+V are not available: Windows already owns both.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkV = 0x56;
    private const int HotkeyId = 1;

    /// <summary>A message-only window's parent handle (HWND_MESSAGE); it is never shown.</summary>
    private static readonly IntPtr HwndMessage = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("ClipVaultHotkeyWindow")
        {
            WindowStyle = 0,
            ParentWindow = HwndMessage,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Returns false when the hotkey is already taken by something else.</summary>
    public bool Register()
    {
        _registered = RegisterHotKey(_source.Handle, HotkeyId, ModWin | ModAlt | ModNoRepeat, VkV);
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
