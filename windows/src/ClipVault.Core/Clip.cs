namespace ClipVault.Core;

/// <summary>Something worth remembering off the clipboard.</summary>
public abstract record Clip
{
    public sealed record TextClip(string Text) : Clip;

    public sealed record ImageClip(ImagePayload Image) : Clip;

    public string Hash() => this switch
    {
        TextClip t => Hashing.Sha256Hex(t.Text),
        ImageClip i => i.Image.Hash,
        _ => throw new InvalidOperationException("unreachable"),
    };
}
