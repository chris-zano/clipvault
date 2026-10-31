using System.IO;
using System.IO.Pipes;
using System.Text;
using ClipVault.Core;

namespace ClipVault.App;

/// <summary>
/// Only one copy of ClipVault ever runs. A second launch hands its command
/// line to the running copy over a named pipe and exits, rather than
/// starting a second daemon - the .NET equivalent of
/// `tauri-plugin-single-instance`.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "Local\\dev.local.clipvault.SingleInstance";
    private const string PipeName = "dev.local.clipvault.ipc";

    /// <summary>
    /// Tries to become the one running copy.
    ///
    /// Returns a handle to keep for the app's lifetime when this is the
    /// first instance (its <c>Dispose</c> releases the single-instance lock
    /// and stops accepting forwarded launches); returns null when another
    /// copy is already running, in which case <paramref name="args"/> has
    /// already been forwarded to it and this process should exit.
    /// </summary>
    /// <param name="onForwarded">
    /// Called with the intent of a later launch's command line. Runs on a
    /// background thread - a WPF-touching handler must marshal through the
    /// dispatcher itself.
    /// </param>
    public static IDisposable? AcquireOrForward(string[] args, Action<Intent> onForwarded)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            StartServer(onForwarded);
            return mutex;
        }

        mutex.Dispose();
        ForwardToRunningInstance(args);
        return null;
    }

    private static void ForwardToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.Write(string.Join('\n', args));
        }
        catch (Exception)
        {
            // The other copy is shutting down or unreachable right now; there
            // is nothing more a second launch can do but let the user retry.
        }
    }

    private static void StartServer(Action<Intent> onForwarded)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync();
                    var args = text.Length == 0 ? [] : text.Split('\n');
                    onForwarded(CommandLine.IntentOf(args));
                }
                catch (Exception)
                {
                    // Keep serving future launches even if one connection
                    // misbehaved.
                }
            }
            // ReSharper disable once FunctionNeverReturns - runs for the app's lifetime
        });
    }
}
