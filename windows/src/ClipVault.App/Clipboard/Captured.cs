namespace ClipVault.App.Clipboard;

/// <summary>
/// What came off the clipboard, still undecoded - images are not run through
/// <c>Media.Prepare</c> until the recorder's background task picks them up,
/// so the listener window's callback never blocks on a multi-megabyte
/// screenshot. Mirrors <c>Captured</c> in <c>watcher.rs</c>.
/// </summary>
internal abstract record Captured
{
    public sealed record Text(string Value) : Captured;

    public sealed record Image(byte[] EncodedBytes) : Captured;
}
