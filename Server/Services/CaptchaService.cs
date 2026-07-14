using System.Security.Cryptography;
using System.Text;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// A tiny self-hosted CAPTCHA: the server issues a simple arithmetic challenge
/// ("7 + 3 = ?") plus a signed, time-limited token that encodes the expected
/// answer. No external service, no per-server state — the token is an HMAC of
/// {answer|expiry}, so any server instance can validate it. Stops casual bots;
/// not intended to defeat determined, targeted attackers.
/// </summary>
public sealed class CaptchaService
{
    private readonly byte[] _key;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public CaptchaService(IConfiguration config)
    {
        // Reuse the JWT secret as the signing key (already a strong app secret).
        var secret = config["Jwt:Secret"] ?? "pm-captcha-fallback-key-change-me";
        _key = Encoding.UTF8.GetBytes(secret);
    }

    public record Challenge(string Question, string Token);

    /// <summary>Generate a fresh arithmetic challenge and its signed token.</summary>
    public Challenge Generate()
    {
        int a = RandomNumberGenerator.GetInt32(1, 10);
        int b = RandomNumberGenerator.GetInt32(1, 10);
        // Mix operations a little
        var op = RandomNumberGenerator.GetInt32(0, 2); // 0=+, 1=*
        int answer = op == 0 ? a + b : a * b;
        string q = op == 0 ? $"{a} + {b}" : $"{a} × {b}";

        long expiry = DateTimeOffset.UtcNow.Add(Ttl).ToUnixTimeSeconds();
        string payload = $"{answer}|{expiry}";
        string sig = Sign(payload);
        // Token = base64(payload).signature  (answer is not human-readable at a glance,
        // but security rests on the HMAC, not on hiding the answer)
        string token = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))}.{sig}";
        return new Challenge($"What is {q}?", token);
    }

    /// <summary>Validate a user's answer against the signed token.</summary>
    public bool Validate(string? token, string? answer)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(answer)) return false;
        var parts = token.Split('.', 2);
        if (parts.Length != 2) return false;

        string payload;
        try { payload = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])); }
        catch { return false; }

        // Verify signature (constant-time)
        var expectedSig = Sign(payload);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedSig), Encoding.UTF8.GetBytes(parts[1])))
            return false;

        var seg = payload.Split('|');
        if (seg.Length != 2) return false;
        if (!int.TryParse(seg[0], out var expected)) return false;
        if (!long.TryParse(seg[1], out var expiry)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry) return false; // expired

        return int.TryParse(answer.Trim(), out var given) && given == expected;
    }

    private string Sign(string payload)
    {
        using var h = new HMACSHA256(_key);
        return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
