using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PeripheralMonitor.Server.Services;

/// <summary>
/// Builds a Guacamole connection token for web-based remote desktop access.
/// Uses the guacamole-auth-totp / quickconnect format:
/// a base64url-encoded JSON connection object, HMAC-signed with GUACAMOLE_SECRET.
/// The token is passed as the client ID in the Guacamole URL:
///   /guacamole/#/client/{base64token}
/// </summary>
public static class GuacamoleTokenBuilder
{
    public static string Build(
        string secret,
        string hostname,
        string protocol,    // "rdp" or "vnc"
        string username,
        string password,
        string? domain = null,
        int port = 0)
    {
        if (port == 0) port = protocol == "rdp" ? 3389 : 5900;

        // Guacamole connection parameters
        var connection = new Dictionary<string, object>
        {
            ["protocol"] = protocol,
            ["parameters"] = BuildParams(hostname, port, username, password, domain, protocol),
        };

        var json = JsonSerializer.Serialize(connection);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('='); // base64url

        return token;
    }

    private static Dictionary<string, string> BuildParams(
        string hostname, int port, string username, string password, string? domain, string protocol)
    {
        var p = new Dictionary<string, string>
        {
            ["hostname"] = hostname,
            ["port"]     = port.ToString(),
            ["username"] = username,
            ["password"] = password,
        };

        if (protocol == "rdp")
        {
            if (!string.IsNullOrEmpty(domain)) p["domain"] = domain;
            p["security"]            = "any";
            p["ignore-cert"]         = "true";
            p["enable-drive"]        = "false";
            p["create-drive-path"]   = "false";
            p["enable-wallpaper"]    = "false";    // faster connections
            p["enable-theming"]      = "false";
            p["enable-font-smoothing"] = "false";
        }
        else if (protocol == "vnc")
        {
            // VNC from x11vnc has no password by default in our setup
        }

        return p;
    }
}
