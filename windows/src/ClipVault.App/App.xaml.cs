using System.Windows;
using ClipVault.App.Clipboard;
using ClipVault.Core;

namespace ClipVault.App;

/// <summary>
/// Entry point and background shell. Ported from <c>run()</c> in <c>lib.rs</c>:
/// single instance, tray, global hotkey, auto-lock sweeper, lazy window.
/// </summary>
public partial class App : System.Windows.Application
{
    private AppState? _state;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkey;
    private IDisposable? _singleInstanceLock;
    private MainWindow? _window;
    private ClipboardWatcher? _clipboardWatcher;
    private ClipboardRecorder? _clipboardRecorder;
    private ClipboardWriter? _clipboardWriter;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.Apply(Resources);

        var args = e.Args;

        if (CommandLine.WantsHelp(args))
        {
            ConsoleInterop.WriteToParentConsole(() => Console.Write(CommandLine.Help));
            Shutdown(0);
            return;
        }

        var intent = CommandLine.IntentOf(args);
        var wantHotkey = CommandLine.WantsHotkey(args);

        var singleInstanceLock = SingleInstance.AcquireOrForward(args, forwardedIntent =>
        {
            // Runs on a background thread; WPF objects must only be touched
            // from the dispatcher thread.
            Dispatcher.Invoke(() =>
            {
                if (forwardedIntent == Intent.Ui) ShowWindow(); else ToggleWindow();
            });
        });
        if (singleInstanceLock is null)
        {
            // Another copy is already running and now has our request; this
            // process has nothing left to do.
            Shutdown(0);
            return;
        }
        _singleInstanceLock = singleInstanceLock;

        var state = new AppState();
        _state = state;
        state.VaultLocked += () => Dispatcher.Invoke(() => _window?.NotifyVaultLocked());

        var tray = new TrayIcon();
        _tray = tray;
        tray.ToggleRequested += () => Dispatcher.Invoke(ToggleWindow);
        tray.LockRequested += () => state.Vault.Lock();
        tray.QuitRequested += () => Shutdown(0);

        var recorder = new ClipboardRecorder(state);
        _clipboardRecorder = recorder;
        recorder.HistoryChanged += () => Dispatcher.Invoke(() => _window?.NotifyHistoryChanged());

        var watcher = new ClipboardWatcher();
        _clipboardWatcher = watcher;
        watcher.CaptureFound += recorder.Enqueue;

        _clipboardWriter = new ClipboardWriter(state.Suppress);

        var hotkey = new HotkeyManager();
        _hotkey = hotkey;
        hotkey.Pressed += ToggleWindow;
        if (wantHotkey && !hotkey.Register())
        {
            // Could not register Win+Alt+V - probably taken by something
            // else. The tray icon still works either way.
        }

        if (intent != Intent.Daemon)
        {
            ShowWindow();
        }
    }

    /// <summary>
    /// The window is built only on first use, not at startup - staying light
    /// until opened is the whole point of starting as a tray daemon.
    /// </summary>
    private MainWindow EnsureWindow() => _window ??= new MainWindow(_state!, _clipboardWriter!);

    private void ShowWindow()
    {
        var window = EnsureWindow();
        CenterOnScreen(window);
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
        window.OnShown();
    }

    private void ToggleWindow()
    {
        if (_window is { IsVisible: true })
        {
            _window.Hide();
        }
        else
        {
            ShowWindow();
        }
    }

    private static void CenterOnScreen(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.Left = area.Left + (area.Width - window.Width) / 2;
        window.Top = area.Top + (area.Height - window.Height) / 2;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _clipboardWatcher?.Dispose();
        _clipboardRecorder?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _state?.Dispose();
        _singleInstanceLock?.Dispose();
        base.OnExit(e);
    }
}
