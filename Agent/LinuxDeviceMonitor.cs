using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace PeripheralMonitor.Agent;

/// <summary>
/// Linux implementation: monitors /sys/bus/* and /dev for device changes.
/// Uses a polling + FileSystemWatcher approach since udev socket interop
/// requires native libs. For production, consider adding a udev rules-based
/// helper that writes events to a named pipe this monitor reads.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxDeviceMonitor : IDeviceMonitor
{
    private readonly ILogger<LinuxDeviceMonitor>             _logger;
    private readonly CancellationTokenSource                 _cts = new();
    private readonly ConcurrentDictionary<string, DeviceInfo> _snapshot = new();
    private readonly ConcurrentDictionary<string, DateTime>  _recentEvents = new();
    private FileSystemWatcher?                               _devWatcher;
    private const int DedupWindowSeconds = 2;

    public event Func<string, DeviceInfo, Task>? DeviceChanged;

    public LinuxDeviceMonitor(ILogger<LinuxDeviceMonitor> logger) => _logger = logger;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await LoadInitialSnapshotAsync();
        _logger.LogInformation("Linux monitor: initial snapshot {Count} devices", _snapshot.Count);

        // Watch /dev for block/char device additions and removals
        StartDevWatcher();

        // Poll /sys/bus every 15s as reconciliation (like Layer 2 on Windows)
        StartSysBusPoller();

        // Read udev named pipe if available (optional enhancement)
        StartUdevPipeReader();

        _logger.LogInformation("LinuxDeviceMonitor started (/dev watcher + /sys/bus poll)");
    }

    public Task<IReadOnlyList<DeviceInfo>> GetSnapshotAsync()
        => Task.FromResult<IReadOnlyList<DeviceInfo>>(_snapshot.Values.ToList());

    // ── /dev FileSystemWatcher ───────────────────────────────────────────
    private void StartDevWatcher()
    {
        try
        {
            _devWatcher = new FileSystemWatcher("/dev")
            {
                NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                EnableRaisingEvents   = true
            };
            _devWatcher.Created += (_, e) => _ = OnDevNodeChanged("Connected",   e.FullPath);
            _devWatcher.Deleted += (_, e) => _ = OnDevNodeChanged("Disconnected", e.FullPath);
            _logger.LogInformation("/dev FileSystemWatcher active");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "/dev watcher failed — falling back to polling only"); }
    }

    private async Task OnDevNodeChanged(string action, string path)
    {
        var device = MapDevPath(path);
        if (device == null) return;

        if (action == "Connected")
            _snapshot[device.DeviceId] = device;
        else
            _snapshot.TryRemove(device.DeviceId, out _);

        await FireEventAsync(action, device);
    }

    // ── /sys/bus poll ────────────────────────────────────────────────────
    private void StartSysBusPoller()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token);
                    await ReconcileAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Sys/bus poll error"); }
            }
        });
    }

    private async Task ReconcileAsync()
    {
        var current = await EnumerateSysBusAsync();
        var arrived = current.Keys.Except(_snapshot.Keys).ToList();
        var removed = _snapshot.Keys.Except(current.Keys).ToList();

        foreach (var id in arrived) { _snapshot[id] = current[id]; await FireEventAsync("Connected",    current[id]); }
        foreach (var id in removed) { var d = _snapshot[id]; _snapshot.TryRemove(id, out _); await FireEventAsync("Disconnected", d); }
    }

    private Task<Dictionary<string, DeviceInfo>> EnumerateSysBusAsync() => Task.Run(() =>
    {
        var result = new Dictionary<string, DeviceInfo>(StringComparer.Ordinal);

        // USB devices via /sys/bus/usb/devices
        EnumerateSysBusSubsystem("/sys/bus/usb/devices",    "USB Device", result);
        EnumerateSysBusSubsystem("/sys/bus/hid/devices",    "HID Device", result);
        EnumerateSysBusSubsystem("/sys/bus/input/devices",  "Input",      result);
        EnumerateSysBusSubsystem("/sys/bus/bluetooth/devices", "Bluetooth", result);
        EnumerateSysBusSubsystem("/sys/bus/pci/devices",    "PCI Device", result);

        return result;
    });

    private void EnumerateSysBusSubsystem(string path, string defaultCategory, Dictionary<string, DeviceInfo> result)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                var id       = Path.GetFileName(dir);
                var name     = ReadSysAttr(dir, "product")
                            ?? ReadSysAttr(dir, "description")
                            ?? ReadSysAttr(dir, "name")
                            ?? id;
                var category = DetectCategory(dir, id, defaultCategory);
                result[id]   = new DeviceInfo(id, name, category, "OK");
            }
        }
        catch { /* directory may not be accessible */ }
    }

    private static string? ReadSysAttr(string dir, string attr)
    {
        var path = Path.Combine(dir, attr);
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static string DetectCategory(string dir, string id, string fallback)
    {
        var modalias = ReadSysAttr(dir, "modalias") ?? "";
        var classStr = ReadSysAttr(dir, "class")    ?? "";
        var lower    = (id + modalias + classStr).ToLowerInvariant();

        if (lower.Contains("keyboard"))  return "Keyboard";
        if (lower.Contains("mouse"))     return "Mouse";
        if (lower.Contains("bluetooth")) return "Bluetooth";
        if (lower.Contains("audio") || lower.Contains("sound")) return "Audio";
        if (lower.Contains("camera") || lower.Contains("video")) return "Camera";
        if (lower.Contains("printer"))   return "Printer";
        if (lower.Contains("storage") || lower.Contains("disk")) return "Storage";
        if (lower.Contains("net") || lower.Contains("ethernet")) return "Network";
        if (lower.Contains("serial") || lower.Contains("uart"))  return "Serial Port";
        return fallback;
    }

    // ── Udev named pipe (optional) ───────────────────────────────────────
    // Create /run/peripheral-monitor/udev-events as a named pipe
    // and write to it from a udev rule: RUN+="sh -c 'echo ... > /run/peripheral-monitor/udev-events'"
    private void StartUdevPipeReader()
    {
        const string pipePath = "/run/peripheral-monitor/udev-events";
        if (!File.Exists(pipePath)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(pipePath);
                while (!_cts.Token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // Expected format: ACTION|DEVPATH|SUBSYSTEM|ID_MODEL
                    var parts = line.Split('|');
                    if (parts.Length < 4) continue;
                    var action   = parts[0] == "add" ? "Connected" : "Disconnected";
                    var devPath  = parts[1];
                    var category = MapSubsystem(parts[2]);
                    var name     = Uri.UnescapeDataString(parts[3].Replace("+", " "));
                    var device   = new DeviceInfo(devPath, name, category, "OK");
                    await FireEventAsync(action, device);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("udev pipe reader ended: {Msg}", ex.Message);
            }
        });
    }

    // ── /dev path classification ─────────────────────────────────────────
    private static DeviceInfo? MapDevPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith(".")) return null;

        var category = name switch
        {
            _ when name.StartsWith("sd") || name.StartsWith("nvme") => "Storage",
            _ when name.StartsWith("tty")  => "Serial Port",
            _ when name.StartsWith("video")=> "Camera",
            _ when name.StartsWith("input")=> "Input Device",
            _ when name.StartsWith("hidraw")=> "HID Device",
            _ when name.StartsWith("bus/usb") => "USB Device",
            _ => null
        };

        if (category == null) return null;
        return new DeviceInfo(path, name, category, "OK");
    }

    private static string MapSubsystem(string subsystem) => subsystem.ToLowerInvariant() switch
    {
        "usb"       => "USB Device",
        "hid"       => "HID Device",
        "input"     => "Input Device",
        "bluetooth" => "Bluetooth",
        "block"     => "Storage",
        "tty"       => "Serial Port",
        "video4linux"=> "Camera",
        "sound"     => "Audio",
        "net"       => "Network",
        _           => subsystem
    };

    // ── Initial snapshot ─────────────────────────────────────────────────
    private async Task LoadInitialSnapshotAsync()
    {
        var devices = await EnumerateSysBusAsync();
        foreach (var kv in devices) _snapshot[kv.Key] = kv.Value;
    }

    // ── De-dup fire ──────────────────────────────────────────────────────
    private async Task FireEventAsync(string action, DeviceInfo device)
    {
        var key = $"{action}:{device.DeviceId}";
        var now = DateTime.UtcNow;
        if (_recentEvents.TryGetValue(key, out var last) && (now - last).TotalSeconds < DedupWindowSeconds) return;
        _recentEvents[key] = now;
        try { if (DeviceChanged != null) await DeviceChanged(action, device); }
        catch (Exception ex) { _logger.LogWarning(ex, "DeviceChanged handler error"); }
    }

    public void Stop()  => _cts.Cancel();

    public void Dispose()
    {
        Stop();
        _devWatcher?.Dispose();
        _cts.Dispose();
    }
}
