using System.Text.Json;

namespace PeripheralMonitor.Agent.Services;

/// <summary>
/// Reads the locally-installed RustDesk client's generated ID from its config file.
/// RustDesk generates a unique ID on first run and stores it in RustDesk2.toml (newer)
/// or RustDesk2.json (older versions) under the user's app-data directory.
///
/// The agent does NOT install or configure RustDesk — that's done by the platform
/// installer (install.ps1 / install.sh) during agent setup, which also sets the
/// fixed connection password and rendezvous server address.
/// </summary>
public class RustDeskService
{
    private readonly ILogger<RustDeskService> _log;
    private string? _cachedId;
    private DateTime _lastCheck = DateTime.MinValue;

    public RustDeskService(ILogger<RustDeskService> log) => _log = log;

    /// <summary>Returns the RustDesk ID for this machine, or null if not installed / not yet generated. Cached for 5 minutes.</summary>
    public string? GetId()
    {
        if (_cachedId != null && DateTime.UtcNow - _lastCheck < TimeSpan.FromMinutes(5))
            return _cachedId;

        _lastCheck = DateTime.UtcNow;
        try
        {
            _cachedId = OperatingSystem.IsWindows() ? GetIdWindows() : GetIdLinux();
            if (_cachedId != null)
                _log.LogInformation("RustDesk ID detected: {Id}", _cachedId);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to read RustDesk ID");
        }
        return _cachedId;
    }

    private string? GetIdWindows()
    {
        // RustDesk (service mode) stores config under C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config
        // or, if running as a normal user install, %APPDATA%\RustDesk\config
        var candidates = new[]
        {
            @"C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config\RustDesk2.toml",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustDesk", "config", "RustDesk2.toml"),
            @"C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config\RustDesk2.json",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RustDesk", "config", "RustDesk2.json"),
        };
        return ReadIdFromFirstExisting(candidates);
    }

    private string? GetIdLinux()
    {
        var candidates = new[]
        {
            "/root/.config/rustdesk/RustDesk2.toml",
            "/var/lib/rustdesk/RustDesk2.toml",
            "/root/.config/rustdesk/RustDesk2.json",
            "/var/lib/rustdesk/RustDesk2.json",
        };
        return ReadIdFromFirstExisting(candidates);
    }

    private string? ReadIdFromFirstExisting(string[] paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var text = File.ReadAllText(path);
                var id = path.EndsWith(".toml")
                    ? ExtractFromToml(text)
                    : ExtractFromJson(text);
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
            catch (Exception ex) { _log.LogDebug(ex, "Could not parse {Path}", path); }
        }
        return null;
    }

    /// <summary>Minimal TOML parsing — RustDesk2.toml has a top-level `id = "123456789"` line.</summary>
    private static string? ExtractFromToml(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("id", StringComparison.OrdinalIgnoreCase)) continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;
            var value = trimmed[(eq + 1)..].Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string? ExtractFromJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("id", out var idEl))
            return idEl.GetString();
        return null;
    }
}
