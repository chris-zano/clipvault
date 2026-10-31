namespace ClipVault.Core;

/// <summary>
/// A vault operation failed in an expected way (wrong password, locked vault,
/// corrupt data). Mirrors the `anyhow::Error` messages `vault.rs` returns to
/// the UI as plain strings.
/// </summary>
public sealed class VaultException(string message) : Exception(message);
