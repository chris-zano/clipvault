using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Geralt;

namespace ClipVault.Core;

/// <summary>
/// Encrypted secrets vault. A direct port of `vault.rs`: same Argon2id
/// parameters, same XChaCha20-Poly1305 construction, same sealed-verifier
/// scheme, so a vault created by either build unlocks in the other
/// (see <c>VaultCrossCheckTests</c>).
/// </summary>
public sealed class Vault
{
    private static readonly byte[] Verifier = Encoding.ASCII.GetBytes("clipvault-verify-v1");
    private const int SaltLen = 16;

    // ~64 MiB, 3 passes. Tuned to cost roughly a quarter second on a laptop,
    // which is cheap once per unlock and expensive to do a few billion times
    // offline.
    private const int KdfMemKib = 65536;
    private const int KdfPasses = 3;
    private const int KdfLanes = 4;

    /// <summary>Minutes of inactivity after which the key is dropped from memory.</summary>
    public const int AutoLockMinutes = 10;

    // .NET strings are immutable and can be copied by the GC/JIT, so unlike
    // the Rust `Zeroizing<String>` we cannot guarantee a decrypted secret's
    // memory is wiped once it is handed back as a string. The key and the
    // intermediate plaintext buffer, which live for the whole session, are
    // the ones we can and do scrub.
    private byte[]? _key;
    private long? _touchedTimestamp;

    public bool IsUnlocked => _key is not null;

    public void Lock()
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
        }
        _key = null;
        _touchedTimestamp = null;
    }

    public void Touch()
    {
        if (_key is not null)
        {
            _touchedTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Drops the key if the vault has gone untouched past the timeout.
    /// Returns true when this call actually locked it.
    /// </summary>
    public bool LockIfIdle(int minutes)
    {
        var idle = _touchedTimestamp is { } ts
            && Stopwatch.GetElapsedTime(ts).TotalSeconds >= minutes * 60;
        if (idle && _key is not null)
        {
            Lock();
            return true;
        }
        return false;
    }

    public long? SecondsUntilLock(int minutes)
    {
        if (_touchedTimestamp is not { } ts)
        {
            return null;
        }
        var remaining = minutes * 60 - Stopwatch.GetElapsedTime(ts).TotalSeconds;
        return (long)Math.Max(0, remaining);
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        var key = GC.AllocateArray<byte>(32, pinned: true);
        using var argon2 = new Konscious.Security.Cryptography.Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = KdfLanes,
            MemorySize = KdfMemKib,
            Iterations = KdfPasses,
        };
        var bytes = argon2.GetBytes(32);
        bytes.CopyTo(key, 0);
        CryptographicOperations.ZeroMemory(bytes);
        return key;
    }

    public void Init(Store store, string password)
    {
        if (store.VaultMeta() is not null)
        {
            throw new VaultException("vault already exists");
        }
        if (password.EnumerateRunes().Count() < 8)
        {
            throw new VaultException("master password must be at least 8 characters");
        }

        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var key = DeriveKey(password, salt);

        var nonce = RandomNumberGenerator.GetBytes(XChaCha20Poly1305.NonceSize);
        var ct = new byte[Verifier.Length + XChaCha20Poly1305.TagSize];
        XChaCha20Poly1305.Encrypt(ct, Verifier, nonce, key, ReadOnlySpan<byte>.Empty);

        store.SetVaultMeta(salt, nonce, ct);
        _key = key;
        Touch();
    }

    public void Unlock(Store store, string password)
    {
        var meta = store.VaultMeta() ?? throw new VaultException("vault has not been set up yet");
        var (salt, nonce, ct) = meta;

        var key = DeriveKey(password, salt);
        if (ct.Length < XChaCha20Poly1305.TagSize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new VaultException("wrong password");
        }

        var plain = new byte[ct.Length - XChaCha20Poly1305.TagSize];
        try
        {
            XChaCha20Poly1305.Decrypt(plain, ct, nonce, key, ReadOnlySpan<byte>.Empty);
        }
        catch (Exception)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new VaultException("wrong password");
        }

        if (!plain.AsSpan().SequenceEqual(Verifier))
        {
            CryptographicOperations.ZeroMemory(key);
            throw new VaultException("wrong password");
        }

        _key = key;
        Touch();
    }

    private byte[] RequireKey() => _key ?? throw new VaultException("vault is locked");

    public (byte[] Nonce, byte[] Ciphertext) Encrypt(string plaintext)
    {
        var key = RequireKey();
        var nonce = RandomNumberGenerator.GetBytes(XChaCha20Poly1305.NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[plainBytes.Length + XChaCha20Poly1305.TagSize];
        try
        {
            XChaCha20Poly1305.Encrypt(ct, plainBytes, nonce, key, ReadOnlySpan<byte>.Empty);
        }
        catch (Exception)
        {
            throw new VaultException("encryption failed");
        }
        return (nonce, ct);
    }

    public string Decrypt(byte[] nonce, byte[] ciphertext)
    {
        var key = RequireKey();
        if (ciphertext.Length < XChaCha20Poly1305.TagSize)
        {
            throw new VaultException("decryption failed - data may be corrupt");
        }

        var plain = new byte[ciphertext.Length - XChaCha20Poly1305.TagSize];
        try
        {
            try
            {
                XChaCha20Poly1305.Decrypt(plain, ciphertext, nonce, key, ReadOnlySpan<byte>.Empty);
            }
            catch (Exception)
            {
                throw new VaultException("decryption failed - data may be corrupt");
            }

            try
            {
                // Strict decode: a secret with invalid UTF-8 must fail, not
                // silently turn into replacement characters, same as Rust's
                // `String::from_utf8`.
                return Utf8Strict.GetString(plain);
            }
            catch (DecoderFallbackException)
            {
                throw new VaultException("secret is not valid UTF-8");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
