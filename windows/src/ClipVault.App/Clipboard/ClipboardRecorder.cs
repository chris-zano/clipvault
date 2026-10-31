using System.Threading.Channels;
using ClipVault.Core;

namespace ClipVault.App.Clipboard;

/// <summary>
/// Clipboard watcher -> store, on its own task, so neither the clipboard
/// listener nor SQLite can stall the other. Decoding and hashing happen here
/// rather than in <see cref="ClipboardWatcher"/>'s callback, so a
/// multi-megabyte screenshot cannot delay the next capture. Mirrors the
/// recorder thread `lib.rs` spawns in its <c>setup</c> closure.
/// </summary>
internal sealed class ClipboardRecorder : IDisposable
{
    private readonly AppState _state;
    private readonly Channel<Captured> _channel = Channel.CreateUnbounded<Captured>();
    private readonly Task _loop;

    /// <summary>Raised (off the UI thread) after a new entry is actually recorded.</summary>
    public event Action? HistoryChanged;

    public ClipboardRecorder(AppState state)
    {
        _state = state;
        _loop = Task.Run(RunAsync);
    }

    public void Enqueue(Captured captured) => _channel.Writer.TryWrite(captured);

    private async Task RunAsync()
    {
        await foreach (var captured in _channel.Reader.ReadAllAsync())
        {
            Clip clip;
            switch (captured)
            {
                case Captured.Text text:
                    clip = new Clip.TextClip(text.Value);
                    break;
                case Captured.Image image:
                    try
                    {
                        clip = new Clip.ImageClip(Media.Prepare(image.EncodedBytes));
                    }
                    catch (Exception)
                    {
                        // unreadable image data; nothing worth keeping
                        continue;
                    }
                    break;
                default:
                    continue;
            }

            if (_state.Suppress.ShouldSkip(clip.Hash()))
            {
                continue;
            }

            try
            {
                if (_state.Store.Record(clip) is not null)
                {
                    HistoryChanged?.Invoke();
                }
            }
            catch (Exception)
            {
                // A later phase adds logging; for now a failed write just
                // means this one capture is lost, not that the app should crash.
            }
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // best-effort drain on shutdown
        }
    }
}
