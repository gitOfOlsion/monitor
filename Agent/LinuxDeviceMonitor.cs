using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace PeripheralMonitor.Agent;

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
        _logger.LogInformation("Linux monitor: {Count} devices in initial snapshot", _snapshot.Count);
        StartDevWatcher();
        StartSysBusPoller();
        StartUdevPipeReader();
    }

    public Task<IReadOnlyList<DeviceInfo>> GetSnapshotAsync()
        => Task.FromResult<IReadOnlyList<DeviceInfo>>(_snapshot.Values.ToList());

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
            _devWatcher.Created += (_, e) => _ = OnDevNode("Connected",    e.FullPath);
            _devWatcher.Deleted += (_, e) => _ = OnDevNode("Disconnected", e.FullPath);
            _logger.LogInformation("/dev watcher active");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "/dev watcher failed"); }
    }

    private async Task OnDevNode(string action, string path)
    {
        var device = MapDevPath(path);
        if (device == null) return;
        if (action == "Connected") _snapshot[device.DeviceId] = device;
        else _snapshot.TryRemove(device.DeviceId, out _);
        await FireAsync(action, device);
    }

    private void StartSysBusPoller()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token); await ReconcileAsync(); }
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
        foreach (var id in arrived) { _snapshot[id] = current[id]; await FireAsync("Connected",    current[id]); }
        foreach (var id in removed) { var d = _snapshot[id]; _snapshot.TryRemove(id, out _); await FireAsync("Disconnected", d); }
    }

    private Task<Dictionary<string, DeviceInfo>> EnumerateSysBusAsync() => Task.Run(() =>
    {
        var result = new Dictionary<string, DeviceInfo>(StringComparer.Ordinal);
        EnumerateSysPath("/sys/bus/usb/devices",       "USB Device", result);
        EnumerateSysPath("/sys/bus/hid/devices",       "HID Device", result);
        EnumerateSysPath("/sys/bus/input/devices",     "Input Device", result);
        EnumerateSysPath("/sys/bus/bluetooth/devices", "Bluetooth", result);
        EnumerateSysPath("/sys/bus/pci/devices",       "PCI Device", result);
        return result;
    });

    private void EnumerateSysPath(string path, string defaultCategory, Dictionary<string, DeviceInfo> result)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                var id       = Path.GetFileName(dir);
                var name     = ReadAttr(dir, "product") ?? ReadAttr(dir, "description") ?? ReadAttr(dir, "name") ?? id;
                var category = DetectCategory(dir, id, defaultCategory);
                result[id]   = new DeviceInfo(id, name, category, "OK");
            }
        }
        catch { }
    }

    private static string? ReadAttr(string dir, string attr)
    {
        try { var p = Path.Combine(dir, attr); return File.Exists(p) ? File.ReadAllText(p).Trim() : null; }
        catch { return null; }
    }

    private static string DetectCategory(string dir, string id, string fallback)
    {
        var lower = (id + (ReadAttr(dir, "modalias") ?? "") + (ReadAttr(dir, "class") ?? "")).ToLowerInvariant();
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
                    var parts = line.Split('|');
                    if (parts.Length < 4) continue;
                    var action   = parts[0] == "add" ? "Connected" : "Disconnected";
                    var category = MapSubsystem(parts[2]);
                    var name     = Uri.UnescapeDataString(parts[3].Replace("+", " "));
                    var device   = new DeviceInfo(parts[1], name, category, "OK");
                    await FireAsync(action, device);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogDebug("udev pipe ended: {Msg}", ex.Message); }
        });
    }

    private static DeviceInfo? MapDevPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith(".")) return null;
        var category = name switch
        {
            _ when name.StartsWith("sd") || name.StartsWith("nvme") => "Storage",
            _ when name.StartsWith("tty")   => "Serial Port",
            _ when name.StartsWith("video") => "Camera",
            _ when name.StartsWith("input") => "Input Device",
            _ when name.StartsWith("hidraw")=> "HID Device",
            _ => null
        };
        return category == null ? null : new DeviceInfo(path, name, category, "OK");
    }

    private static string MapSubsystem(string s) => s.ToLowerInvariant() switch
    {
        "usb"        => "USB Device",
        "hid"        => "HID Device",
        "input"      => "Input Device",
        "bluetooth"  => "Bluetooth",
        "block"      => "Storage",
        "tty"        => "Serial Port",
        "video4linux"=> "Camera",
        "sound"      => "Audio",
        "net"        => "Network",
        _            => "Other"
    };

    private async Task LoadInitialSnapshotAsync()
    {
        var devices = await EnumerateSysBusAsync();
        foreach (var kv in devices) _snapshot[kv.Key] = kv.Value;
    }

    private async Task FireAsync(string action, DeviceInfo device)
    {
        var key = $"{action}:{device.DeviceId}";
        var now = DateTime.UtcNow;
        if (_recentEvents.TryGetValue(key, out var last) && (now - last).TotalSeconds < DedupWindowSeconds) return;
        _recentEvents[key] = now;
        try { if (DeviceChanged != null) await DeviceChanged(action, device); }
        catch (Exception ex) { _logger.LogWarning(ex, "DeviceChanged error"); }
    }

    public void Stop()  => _cts.Cancel();
    public void Dispose() { Stop(); _devWatcher?.Dispose(); _cts.Dispose(); }
}
