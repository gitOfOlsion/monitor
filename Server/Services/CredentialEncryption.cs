using System.Security.Cryptography;
using System.Text;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// AES-256-CBC encryption for RDP credentials stored in the database.
/// The encryption key is derived from GUACAMOLE_SECRET in .env so it's
/// never stored alongside the encrypted data.
/// </summary>
public static class CredentialEncryption
{
    private static byte[] GetKey()
    {
        // Derive a 256-bit key from the Guacamole secret via SHA-256
        var secret = Environment.GetEnvironmentVariable("GuacamoleSecret")
                  ?? Environment.GetEnvironmentVariable("GUACAMOLE_SECRET")
                  ?? "fallback-dev-key-change-in-production";
        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public static string Encrypt(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = GetKey();
        aes.GenerateIV();

        using var enc = aes.CreateEncryptor();
        var plain = Encoding.UTF8.GetBytes(plainText);
        var cipher = enc.TransformFinalBlock(plain, 0, plain.Length);

        // Prefix IV to ciphertext so we can decrypt later
        var result = new byte[aes.IV.Length + cipher.Length];
        aes.IV.CopyTo(result, 0);
        cipher.CopyTo(result, aes.IV.Length);

        return Convert.ToBase64String(result);
    }

    public static string Decrypt(string encrypted)
    {
        var data = Convert.FromBase64String(encrypted);
        using var aes = Aes.Create();
        aes.Key = GetKey();

        var iv = data[..16];
        var cipher = data[16..];
        aes.IV = iv;

        using var dec = aes.CreateDecryptor();
        var plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }
}
