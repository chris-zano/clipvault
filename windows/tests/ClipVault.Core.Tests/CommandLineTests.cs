using ClipVault.Core;

namespace ClipVault.Core.Tests;

// Ported from the tests in src-tauri/src/lib.rs.
public class CommandLineTests
{
    [Fact]
    public void NoArgumentsMeansSitInTheTray()
    {
        Assert.Equal(Intent.Daemon, CommandLine.IntentOf([]));
        // Flags that are not commands must not change that.
        Assert.Equal(Intent.Daemon, CommandLine.IntentOf(["--no-hotkey"]));
    }

    [Fact]
    public void UiAndToggleAreRecognised()
    {
        Assert.Equal(Intent.Ui, CommandLine.IntentOf(["ui"]));
        Assert.Equal(Intent.Ui, CommandLine.IntentOf(["--ui"]));
        Assert.Equal(Intent.Toggle, CommandLine.IntentOf(["--toggle"]));
        Assert.Equal(Intent.Toggle, CommandLine.IntentOf(["-t"]));
    }

    [Fact]
    public void ACommandIsFoundAmongOtherFlags()
    {
        Assert.Equal(Intent.Ui, CommandLine.IntentOf(["--no-hotkey", "ui"]));
    }

    [Fact]
    public void UnknownArgumentsFallBackToTheDaemon()
    {
        Assert.Equal(Intent.Daemon, CommandLine.IntentOf(["--what", "nonsense"]));
    }
}
