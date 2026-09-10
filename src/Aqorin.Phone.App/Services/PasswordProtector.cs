using System.Security.Cryptography;
using System.Text;

namespace Aqorin.Phone.App.Services;

/// <summary>Encrypts/decrypts the stored SIP password for the current OS user.</summary>
public interface IPasswordProtector
{
    /// <summary>Identifier written next to the ciphertext so a file protected on another OS is recognised.</summary>
    string Scheme { get; }

    string Description { get; }

    byte[] Protect(string plaintext);

    string Unprotect(byte[] ciphertext);
}

/// <summary>Windows: DPAPI, scoped to the current user account (no key material to manage).</summary>
public sealed class DpapiPasswordProtector : IPasswordProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Aqorin.Phone.SipPassword.v1");

    public string Scheme => "dpapi-user";

    public string Description => "Stored encrypted with Windows Data Protection (DPAPI) for your Windows user account.";

    public byte[] Protect(string plaintext) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] ciphertext) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser));
}

/// <summary>
/// macOS/Linux: AES-256-GCM with a random key kept in a file only the current user can read (mode 0600) inside the
/// per-user configuration directory. This protects against casual reading and backups leaking the password, but not
/// against another process running as the same user — a Keychain/libsecret integration is a later improvement.
/// </summary>
public sealed class UserKeyFilePasswordProtector : IPasswordProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _keyPath;

    public UserKeyFilePasswordProtector(string directory)
    {
        _keyPath = Path.Combine(directory, ".credential-key");
    }

    public string Scheme => "aes-gcm-userkey";

    public string Description => "Stored encrypted (AES-256-GCM) with a key file that only your user account can read.";

    public byte[] Protect(string plaintext)
    {
        var key = LoadOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var result = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        cipher.CopyTo(result, NonceSize + TagSize);
        return result;
    }

    public string Unprotect(byte[] ciphertext)
    {
        if (ciphertext.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Stored password is truncated.");
        }

        var key = LoadOrCreateKey();
        var nonce = ciphertext.AsSpan(0, NonceSize);
        var tag = ciphertext.AsSpan(NonceSize, TagSize);
        var cipher = ciphertext.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length == 32)
            {
                return existing;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_keyPath, key);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return key;
    }
}
