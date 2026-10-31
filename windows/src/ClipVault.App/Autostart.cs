using Microsoft.Win32;

namespace ClipVault.App;

/// <summary>
/// Starting at login via <c>HKCU\...\CurrentVersion\Run</c> - the opt-in
/// toggle in the tray menu PLAN.md calls for, standing in for the Rust
/// README's manual <c>~/.config/autostart/*.desktop</c> entry. Launches
/// plain <c>clipvault.exe</c> with no arguments: the daemon, hotkey included -
/// there is no GNOME-style conflict to work around on Windows, so there is
/// no Windows equivalent of the README's <c>--no-hotkey</c> advice to give.
/// </summary>
internal static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipVault";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
        {
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("could not determine the running executable's path");
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
