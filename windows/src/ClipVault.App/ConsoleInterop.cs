using System.Runtime.InteropServices;

namespace ClipVault.App;

/// <summary>
/// A WinExe has no console of its own. <c>--help</c> should still print
/// somewhere useful when launched from a terminal, so this attaches to the
/// launching console (if any) just long enough to write to it.
/// </summary>
internal static class ConsoleInterop
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    /// <summary>
    /// Writes somewhere the launcher can see: straight to stdout when it is
    /// already redirected (piped to a file, captured by a script), or by
    /// attaching to the launching console and detaching again afterwards.
    /// Does nothing when launched without either - double-clicked from
    /// Explorer, or from a shortcut.
    /// </summary>
    public static void WriteToParentConsole(Action write)
    {
        if (Console.IsOutputRedirected)
        {
            // A WinExe still gets real stdout/stderr handles when the
            // launcher redirected them, even though it has no console to
            // attach to - nothing more to do but write.
            write();
            return;
        }
        if (!AttachConsole(AttachParentProcess))
        {
            return;
        }
        try
        {
            write();
        }
        finally
        {
            FreeConsole();
        }
    }
}
