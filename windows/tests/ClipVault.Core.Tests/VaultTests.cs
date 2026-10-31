using ClipVault.Core;
using ClipVault.Core.Tests.TestSupport;

namespace ClipVault.Core.Tests;

// Ported from the #[cfg(test)] module in src-tauri/src/vault.rs.
public sealed class VaultTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Store _store;
    private readonly Vault _vault = new();

    public VaultTests()
    {
        _store = Store.Open(Path.Combine(_dir.Path, "v.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void StartsLocked()
    {
        Assert.False(_vault.IsUnlocked);
        // A locked vault must not encrypt.
        Assert.Throws<VaultException>(() => _vault.Encrypt("nope"));
    }

    [Fact]
    public void InitThenRoundtrip()
    {
        _vault.Init(_store, "correct horse battery");
        Assert.True(_vault.IsUnlocked);

        var (nonce, ct) = _vault.Encrypt("sk-live-abc123");
        Assert.NotEqual("sk-live-abc123"u8.ToArray(), ct); // ciphertext must not be the plaintext
        Assert.Equal("sk-live-abc123", _vault.Decrypt(nonce, ct));
    }

    [Fact]
    public void RejectsShortPassword()
    {
        Assert.Throws<VaultException>(() => _vault.Init(_store, "short"));
        Assert.False(_vault.IsUnlocked);
    }

    [Fact]
    public void UnlockRequiresTheRightPassword()
    {
        _vault.Init(_store, "correct horse battery");
        var (nonce, ct) = _vault.Encrypt("my secret");
        _vault.Lock();
        Assert.False(_vault.IsUnlocked);

        Assert.Throws<VaultException>(() => _vault.Unlock(_store, "wrong password here"));
        Assert.False(_vault.IsUnlocked); // a failed unlock must leave it locked

        _vault.Unlock(_store, "correct horse battery");
        Assert.Equal("my secret", _vault.Decrypt(nonce, ct));
    }

    [Fact]
    public void LockingForgetsTheKey()
    {
        _vault.Init(_store, "correct horse battery");
        var (nonce, ct) = _vault.Encrypt("my secret");
        _vault.Lock();
        Assert.Throws<VaultException>(() => _vault.Decrypt(nonce, ct));
    }

    [Fact]
    public void NoncesDifferPerEncryption()
    {
        _vault.Init(_store, "correct horse battery");
        var (n1, c1) = _vault.Encrypt("same text");
        var (n2, c2) = _vault.Encrypt("same text");
        Assert.NotEqual(n1, n2); // reusing a nonce would leak the plaintext
        Assert.NotEqual(c1, c2);
    }

    [Fact]
    public void TamperedCiphertextIsRejected()
    {
        _vault.Init(_store, "correct horse battery");
        var (nonce, ct) = _vault.Encrypt("my secret");
        ct[0] ^= 0xFF;
        // AEAD must catch the edit.
        Assert.Throws<VaultException>(() => _vault.Decrypt(nonce, ct));
    }

    [Fact]
    public void CannotInitTwice()
    {
        _vault.Init(_store, "correct horse battery");
        var other = new Vault();
        Assert.Throws<VaultException>(() => other.Init(_store, "a different one"));
    }

    [Fact]
    public void IdleTimeoutLocks()
    {
        _vault.Init(_store, "correct horse battery");
        Assert.False(_vault.LockIfIdle(10)); // not idle yet
        Assert.True(_vault.LockIfIdle(0)); // zero-minute timeout locks immediately
        Assert.False(_vault.IsUnlocked);
        Assert.False(_vault.LockIfIdle(0)); // already locked, nothing to do
    }
}
