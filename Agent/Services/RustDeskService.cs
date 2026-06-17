namespace PeripheralMonitor.Agent.Services;

/// <summary>
/// Reads the RustDesk ID for this machine from a plain text file written by the
/// installer (install.ps1 / install.sh) at setup time.
///
/// IMPORTANT: we do NOT try to read RustDesk's own config files at runtime.
/// Current RustDesk versions store the ID as an encrypted "enc_id" field with
/// no documented way to decrypt it outside RustDesk's own process, and the only
/// reliable way to retrieve the plain ID is "rustdesk.exe --get-id" run once at
/// install time (the official RustDesk deployment pattern). The installer captures
/// it then and writes it to "rustdesk-id.txt" next to the agent executable; this
/// service just reads that file.
/// </summary>
public class RustDeskService
{
    private readonly ILogger<RustDeskService> _log;
    private string? _cachedId;
    private DateTime _lastCheck = DateTime.MinValue;

    public RustDeskService(ILogger<RustDeskService> log) => _log = log;

    /// <summary>Returns the RustDesk ID for this machine, or null if not available. Cached for 5 minutes.</summary>
    public string? GetId()
    {
        if (_cachedId != null && DateTime.UtcNow - _lastCheck < TimeSpan.FromMinutes(5))
            return _cachedId;

        _lastCheck = DateTime.UtcNow;
        try
        {
            var idFilePath = Path.Combine(AppContext.BaseDirectory, "rustdesk-id.txt");
            if (File.Exists(idFilePath))
            {
                var text = File.ReadAllText(idFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _cachedId = text;
                    _log.LogDebug("RustDesk ID loaded from {Path}: {Id}", idFilePath, text);
                    return _cachedId;
                }
            }
            _log.LogDebug("No rustdesk-id.txt found at {Path} - RustDesk may not be installed/configured yet", idFilePath);
            _cachedId = null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to read RustDesk ID file");
            _cachedId = null;
        }
        return _cachedId;
    }
}
