using System.Security.Cryptography;
using System.Text;

namespace Aqorin.Phone.App.Services;

public interface IPasswordProtector
{
    string Scheme { get; }

    string Description { get; }

    byte[] Protect(string plaintext);

    string Unprotect(byte[] ciphertext);
}

public sealed class DpapiPasswordProtector : IPasswordProtector
{
    public string Scheme => "mobile-unavailable-dpapi";

    public string Description => "Windows DPAPI is not available in the mobile app.";

    public byte[] Protect(string plaintext) =>
        throw new PlatformNotSupportedException("Windows DPAPI is not available in the mobile app.");

    public string Unprotect(byte[] ciphertext) =>
        throw new PlatformNotSupportedException("Windows DPAPI is not available in the mobile app.");
}

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

    public string Description => "Stored encrypted (AES-256-GCM) with an app-local key file.";

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
        return key;
    }
}
