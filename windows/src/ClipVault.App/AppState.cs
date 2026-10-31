using System.IO;
using ClipVault.Core;
using CoreVault = ClipVault.Core.Vault;

namespace ClipVault.App;

/// <summary>
/// App-wide state: the clipboard history store, the secrets vault, and the
/// write suppressor, plus the background auto-lock sweeper. Mirrors the
/// `App` struct that `commands.rs` manages through Tauri's `state::&lt;App&gt;()`,
/// and the sweeper thread `lib.rs` spawns at startup.
/// </summary>
public sealed class AppState : IDisposable
{
    private const int SweepIntervalSeconds = 15;

    public Store Store { get; }
    public CoreVault Vault { get; } = new();
    public Suppressor Suppress { get; } = new();

    private readonly PeriodicTimer _sweepTimer = new(TimeSpan.FromSeconds(SweepIntervalSeconds));
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sweepTask;

    /// <summary>
    /// Raised when the sweeper auto-locks the vault, from the sweep task's
    /// own thread - not the UI thread. A subscriber that touches WPF objects
    /// must marshal through <c>Dispatcher.Invoke</c> itself.
    /// </summary>
    public event Action? VaultLocked;

    public AppState()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipVault");
        Directory.CreateDirectory(dataDir);
        Store = Store.Open(Path.Combine(dataDir, "clipvault.db"));

        _sweepTask = RunAutoLockSweeperAsync(_cts.Token);
    }

    /// <summary>
    /// Applies the idle timeout before any vault operation, so a vault left
    /// open behind a locked screen is already shut by the time it is asked
    /// for. Mirrors <c>App::vault_checked</c> in <c>commands.rs</c>.
    /// </summary>
    public CoreVault VaultChecked()
    {
        Vault.LockIfIdle(CoreVault.AutoLockMinutes);
        return Vault;
    }

    /// <summary>
    /// Without this, the vault would only lock on the next command, which
    /// may never come while the window sits idle.
    /// </summary>
    private async Task RunAutoLockSweeperAsync(CancellationToken token)
    {
        try
        {
            while (await _sweepTimer.WaitForNextTickAsync(token))
            {
                if (Vault.LockIfIdle(CoreVault.AutoLockMinutes))
                {
                    VaultLocked?.Invoke();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _sweepTimer.Dispose();
        try
        {
            _sweepTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // expected: the sweep task observed the cancellation
        }
        _cts.Dispose();
        Store.Dispose();
    }
}
