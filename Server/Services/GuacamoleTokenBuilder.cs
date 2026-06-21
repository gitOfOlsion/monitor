using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// Implements Apache Guacamole's "Encrypted JSON authentication" extension
/// (guacamole-auth-json), which is built into the official Docker image and
/// enabled by setting the JSON_SECRET_KEY environment variable.
///
/// This lets our server authenticate a one-off, ephemeral Guacamole session
/// directly — no Guacamole user database, no login screen — by:
///   1. Building a JSON payload describing the connection (protocol/host/creds)
///   2. Signing it with HMAC-SHA256 using the shared 128-bit key
///   3. Encrypting (signature + JSON) with AES-128-CBC, zero IV
///   4. Base64-encoding the result
///   5. POSTing that as "data" to Guacamole's /api/tokens REST endpoint
///   6. Guacamole returns a real authToken we then use to build the client URL
///
/// Reference: https://guacamole.apache.org/doc/gug/json-auth.html
/// </summary>
public static class GuacamoleTokenBuilder
{
    /// <summary>
    /// Builds the base64-encoded encrypted JSON payload to POST to /api/tokens.
    /// jsonKey must be exactly 32 hex digits (128 bits) - the same value as
    /// the GUACAMOLE_JSON_KEY / JSON_SECRET_KEY env var.
    /// </summary>
    public static string BuildEncryptedPayload(
        string jsonKeyHex,
        string connectionName,
        string protocol,
        string hostname,
        int port,
        string username,
        string password,
        string? domain,
        TimeSpan validFor)
    {
        var expiresMs = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeMilliseconds();

        var parameters = new Dictionary<string, string>
        {
            ["hostname"] = hostname,
            ["port"]     = port.ToString(),
            ["username"] = username,
            ["password"] = password,
        };

        if (protocol == "rdp")
        {
            if (!string.IsNullOrEmpty(domain)) parameters["domain"] = domain;
            parameters["security"]              = "nla";
            parameters["ignore-cert"]            = "true";
            parameters["enable-drive"]           = "false";
            parameters["create-drive-path"]      = "false";
            parameters["enable-wallpaper"]        = "false";
            parameters["enable-theming"]          = "false";
            parameters["enable-font-smoothing"]   = "false";
        }

        var payload = new
        {
            username = "", // anonymous - JSON auth ties the session to the token itself, not a named user
            expires = expiresMs,
            connections = new Dictionary<string, object>
            {
                [connectionName] = new { protocol, parameters }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        return EncryptAndSign(jsonKeyHex, json);
    }

    private static string EncryptAndSign(string keyHex, string json)
    {
        var key = Convert.FromHexString(keyHex);
        if (key.Length != 16)
            throw new InvalidOperationException($"GUACAMOLE_JSON_KEY must be exactly 32 hex digits (128 bits), got {keyHex.Length} characters.");

        var jsonBytes = Encoding.UTF8.GetBytes(json);

        // 1. Sign with HMAC-SHA256
        using var hmac = new HMACSHA256(key);
        var signature = hmac.ComputeHash(jsonBytes);

        // 2. Prepend signature to plaintext JSON
        var combined = new byte[signature.Length + jsonBytes.Length];
        signature.CopyTo(combined, 0);
        jsonBytes.CopyTo(combined, signature.Length);

        // 3. Encrypt with AES-128-CBC, zero IV (as required by guacamole-auth-json)
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = new byte[16]; // all zero bytes
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(combined, 0, combined.Length);

        // 4. Base64 encode
        return Convert.ToBase64String(encrypted);
    }
}
