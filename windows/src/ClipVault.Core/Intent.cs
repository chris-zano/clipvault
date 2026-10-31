namespace ClipVault.Core;

/// <summary>
/// What a command line asked for. A second launch does not start a second
/// copy; it tells the running one what to do.
/// </summary>
public enum Intent
{
    /// <summary>Sit in the tray. What autostart wants.</summary>
    Daemon,
    /// <summary>Bring the window up.</summary>
    Ui,
    /// <summary>Show it, or hide it if it is already up.</summary>
    Toggle,
}

public static class CommandLine
{
    public const string Help = """
        ClipVault - clipboard history with an encrypted secrets vault

        USAGE:
            clipvault [COMMAND] [OPTIONS]

        COMMANDS:
            (none)          Start in the system tray, or toggle the window if already
                            running. This is what an autostart entry should use.
            ui              Open the window, starting the app first if needed.

        OPTIONS:
            -t, --toggle    Show the window, or hide it if it is already showing.
                --no-hotkey Do not register Win+Alt+V. Use this when another
                            tool is bound to `clipvault ui` instead, so the two do not
                            fight over the same key.
            -h, --help      Print this help.

        Only one copy ever runs. Launching it again signals the running one.
        """;

    public static Intent IntentOf(IEnumerable<string> args)
    {
        foreach (var a in args)
        {
            switch (a)
            {
                case "ui" or "--ui":
                    return Intent.Ui;
                case "--toggle" or "-t":
                    return Intent.Toggle;
            }
        }
        return Intent.Daemon;
    }

    public static bool WantsHelp(IEnumerable<string> args) => args.Any(a => a is "--help" or "-h");

    public static bool WantsHotkey(IEnumerable<string> args) => !args.Contains("--no-hotkey");
}
