using ClipVault.Core;
using ClipVault.Core.Tests.TestSupport;

namespace ClipVault.Core.Tests;

/// <summary>
/// Proves the .NET vault and the Rust vault are interoperable: these bytes
/// were produced by the real `clipvault_lib` on this machine (rustc 1.99.1,
/// Argon2id via the `argon2` crate, XChaCha20-Poly1305 via the
/// `chacha20poly1305` crate), captured with a temporary test added to
/// src-tauri/src/vault.rs and reverted immediately after - see PLAN.md for
/// how to regenerate this fixture.
///
/// If the .NET port's Argon2id parameters (memory/passes/lanes) or its
/// XChaCha20-Poly1305 wiring ever drift from the Rust side, this is the test
/// that catches it: a `clipvault.db` written by one build must still unlock
/// in the other.
/// </summary>
public class VaultCrossCheckTests
{
    private const string Password = "cross-check password";
    private const string Secret = "a secret for the cross-check";

    private static readonly byte[] Salt = Hex("7dbd9198e55c5fe0a052dca2226a1145");
    private static readonly byte[] VerifyNonce = Hex("2a0c416d22f516bef58f938ad49a512308f24eb533f35c2e");
    private static readonly byte[] VerifyCiphertext = Hex("47f6167c3c3e683edbe21eb8a9c19558638550506cb1b905223b1030b7af2825879b90");
    private static readonly byte[] SecretNonce = Hex("27d5368360dd530abf289d62e727caabfa3b31760fa1920e");
    private static readonly byte[] SecretCiphertext = Hex("8ebc4837c711a06af623147658a1b3c45de229c6ec6f08ea0ecafc082ef4faaff4b3b10f2aec0acca4212b56");

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void UnlocksAVaultCreatedByTheRustBuild()
    {
        using var dir = new TempDir();
        using var store = Store.Open(Path.Combine(dir.Path, "rust-fixture.db"));
        store.SetVaultMeta(Salt, VerifyNonce, VerifyCiphertext);

        var vault = new Vault();
        // Succeeding here proves the KDF (Argon2id: 64 MiB, 3 passes, 4 lanes)
        // and the AEAD (XChaCha20-Poly1305) agree byte-for-byte with Rust.
        vault.Unlock(store, Password);
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public void DecryptsASecretEncryptedByTheRustBuild()
    {
        using var dir = new TempDir();
        using var store = Store.Open(Path.Combine(dir.Path, "rust-fixture.db"));
        store.SetVaultMeta(Salt, VerifyNonce, VerifyCiphertext);

        var vault = new Vault();
        vault.Unlock(store, Password);

        Assert.Equal(Secret, vault.Decrypt(SecretNonce, SecretCiphertext));
    }
}
