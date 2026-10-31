namespace ClipVault.Core;

/// <summary>
/// Lets the app suppress capture of clipboard writes it made itself, so that
/// pasting from history does not create a duplicate entry and a secret
/// copied out of the vault never reaches the plaintext history table.
/// Ported from the `Suppressor` in `watcher.rs`.
/// </summary>
public sealed class Suppressor
{
    private const long WindowMs = 3000;

    private readonly object _lock = new();
    private (string? Hash, long Deadline)? _pending;

    /// <summary>Skip the next capture only if its content matches <paramref name="text"/>.</summary>
    public void Expect(string text) => ExpectHash(Store.HashOf(text));

    /// <summary>Skip the next capture only if it hashes to <paramref name="hash"/>.</summary>
    public void ExpectHash(string hash)
    {
        lock (_lock)
        {
            _pending = (hash, Environment.TickCount64 + WindowMs);
        }
    }

    /// <summary>Skip the next capture whatever it contains. Used for vault secrets.</summary>
    public void ExpectAny()
    {
        lock (_lock)
        {
            _pending = (null, Environment.TickCount64 + WindowMs);
        }
    }

    public bool ShouldSkip(string hash)
    {
        lock (_lock)
        {
            if (_pending is not { } pending)
            {
                return false;
            }

            // The deadline keeps a suppression that never fired from
            // swallowing an unrelated copy later on.
            if (Environment.TickCount64 > pending.Deadline)
            {
                _pending = null;
                return false;
            }
            if (pending.Hash is null)
            {
                _pending = null;
                return true;
            }
            if (pending.Hash == hash)
            {
                _pending = null;
                return true;
            }
            return false;
        }
    }

    /// <summary>Test-only: arms a suppression whose deadline has already passed.</summary>
    internal void ArmStaleForTest()
    {
        lock (_lock)
        {
            _pending = (null, Environment.TickCount64 - 1000);
        }
    }
}
