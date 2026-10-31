namespace ClipVault.Core.Tests.TestSupport;

/// <summary>Minimal self-cleaning temp directory for the tests, ported from store.rs's `tempdir` module.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"clipvault-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup only
        }
    }
}
