namespace ClipVault.Core;

/// <summary>
/// A decoded, normalised image ready to be recorded. Built by
/// <c>Media.Prepare</c> (phase 2); <see cref="Store"/> only stores and
/// retrieves these bytes, and never decodes or re-encodes them itself.
/// </summary>
/// <param name="Png">The image normalised to PNG.</param>
/// <param name="ThumbPng">A small PNG for the history list thumbnail.</param>
/// <param name="Width">Pixel width of the original image.</param>
/// <param name="Height">Pixel height of the original image.</param>
/// <param name="Hash">
/// Identity hash over the decoded pixels, not the encoded file, so the same
/// picture re-encoded on its way back out of the clipboard still dedupes
/// and still matches the writer's echo-suppression.
/// </param>
public sealed record ImagePayload(byte[] Png, byte[] ThumbPng, uint Width, uint Height, string Hash);
