namespace ClipVault.Core;

/// <summary>One row of clipboard history, as shown in the list.</summary>
public sealed record Entry(
    long Id,
    string Kind,
    string Preview,
    bool Truncated,
    long Bytes,
    bool Pinned,
    long CreatedAt,
    long LastUsedAt,
    long UseCount,
    long? Width,
    long? Height,
    /// <summary>Data URL for the list thumbnail; images only.</summary>
    string? Thumb);

public sealed record VaultItem(long Id, string Label, long CreatedAt);
