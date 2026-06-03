using System.Collections.Concurrent;
using System.Runtime.Versioning;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent;

[SupportedOSPlatform("linux")]
public sealed class LinuxDeviceMonitor : IDeviceMonitor
{
    private readonly ILogger<LinuxDeviceMonitor> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, DeviceInfo> _snap = new();
    private readonly ConcurrentDictionary<string, DateTime> _dedup = new();
    private FileSystemWatcher? _watcher;

    public event Func<string, DeviceInfo, Task>? DeviceChanged;
    public LinuxDeviceMonitor(ILogger<LinuxDeviceMonitor> log) => _log = log;

    public async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var kv in SysBus()) _snap[kv.Key] = kv.Value;
        _log.LogInformation("Linux monitor: {N} devices", _snap.Count);
        StartDevWatcher(); StartPoller(); StartUdevPipe();
    }

    public Task<IReadOnlyList<DeviceInfo>> GetSnapshotAsync()
        => Task.FromResult<IReadOnlyList<DeviceInfo>>(_snap.Values.ToList());

    private void StartDevWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher("/dev") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, IncludeSubdirectories = true, EnableRaisingEvents = true };
            _watcher.Created += (_, e) => _ = OnDev("Connected",    e.FullPath);
            _watcher.Deleted += (_, e) => _ = OnDev("Disconnected", e.FullPath);
        }
        catch (Exception ex) { _log.LogWarning(ex, "/dev watcher failed"); }
    }

    private async Task OnDev(string action, string path)
    {
        var dev = ClassifyDev(path); if (dev == null) return;
        if (action == "Connected") _snap[dev.DeviceId] = dev; else _snap.TryRemove(dev.DeviceId, out _);
        await FireAsync(action, dev);
    }

    private void StartPoller() => _ = Task.Run(async () =>
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try { await Task.Delay(15_000, _cts.Token); await ReconcileAsync(); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Poller error"); }
        }
    });

    private async Task ReconcileAsync()
    {
        var cur = SysBus();
        foreach (var id in cur.Keys.Except(_snap.Keys)) { _snap[id] = cur[id]; await FireAsync("Connected",    cur[id]); }
        foreach (var id in _snap.Keys.Except(cur.Keys)) { var d = _snap[id]; _snap.TryRemove(id, out _); await FireAsync("Disconnected", d); }
    }

    private Dictionary<string, DeviceInfo> SysBus()
    {
        var r = new Dictionary<string, DeviceInfo>();
        foreach (var (path, cat) in new[] { ("/sys/bus/usb/devices","USB Device"), ("/sys/bus/hid/devices","HID Device"), ("/sys/bus/input/devices","Input Device"), ("/sys/bus/bluetooth/devices","Bluetooth") })
        {
            if (!Directory.Exists(path)) continue;
            try { foreach (var dir in Directory.GetDirectories(path))
            {
                var id = Path.GetFileName(dir);
                r[id]  = new DeviceInfo(id, Attr(dir,"product") ?? Attr(dir,"name") ?? id, DetectCat(dir, id, cat), "OK");
            } } catch { }
        }
        return r;
    }

    private void StartUdevPipe()
    {
        const string p = "/run/peripheral-monitor/udev-events";
        if (!File.Exists(p)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var r = new StreamReader(p);
                while (!_cts.Token.IsCancellationRequested)
                {
                    var line = await r.ReadLineAsync(_cts.Token);
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|'); if (parts.Length < 4) continue;
                    var action = parts[0] == "add" ? "Connected" : "Disconnected";
                    await FireAsync(action, new DeviceInfo(parts[1], Uri.UnescapeDataString(parts[3].Replace("+", " ")), MapSub(parts[2]), "OK"));
                }
            }
            catch { }
        });
    }

    private static DeviceInfo? ClassifyDev(string path)
    {
        var n = Path.GetFileName(path); if (n.StartsWith('.')) return null;
        var cat = n switch
        {
            _ when n.StartsWith("sd") || n.StartsWith("nvme") => "Storage",
            _ when n.StartsWith("tty")    => "Serial/COM Port",
            _ when n.StartsWith("video")  => "Camera",
            _ when n.StartsWith("hidraw") => "HID Device",
            _ when n.StartsWith("input")  => "Input Device",
            _ => null
        };
        return cat == null ? null : new DeviceInfo(path, n, cat, "OK");
    }

    private static string? Attr(string dir, string a) { try { var p = Path.Combine(dir, a); return File.Exists(p) ? File.ReadAllText(p).Trim() : null; } catch { return null; } }

    private static string DetectCat(string dir, string id, string fb)
    {
        var s = (id + (Attr(dir,"modalias") ?? "") + (Attr(dir,"class") ?? "")).ToLowerInvariant();
        if (s.Contains("keyboard")) return "Keyboard"; if (s.Contains("mouse")) return "Mouse";
        if (s.Contains("bluetooth")) return "Bluetooth"; if (s.Contains("audio") || s.Contains("sound")) return "Audio";
        if (s.Contains("camera") || s.Contains("video")) return "Camera"; if (s.Contains("printer")) return "Printer";
        if (s.Contains("storage") || s.Contains("disk")) return "Storage"; if (s.Contains("net")) return "Network";
        if (s.Contains("serial") || s.Contains("uart")) return "Serial/COM Port";
        return fb;
    }

    private static string MapSub(string s) => s.ToLowerInvariant() switch
    {
        "usb" => "USB Device", "hid" => "HID Device", "input" => "Input Device",
        "bluetooth" => "Bluetooth", "block" => "Storage", "tty" => "Serial/COM Port",
        "video4linux" => "Camera", "sound" => "Audio", "net" => "Network", _ => s
    };

    private async Task FireAsync(string action, DeviceInfo dev)
    {
        var key = $"{action}:{dev.DeviceId}"; var now = DateTime.UtcNow;
        if (_dedup.TryGetValue(key, out var last) && (now - last).TotalSeconds < 2) return;
        _dedup[key] = now;
        try { if (DeviceChanged != null) await DeviceChanged(action, dev); } catch (Exception ex) { _log.LogWarning(ex, "DeviceChanged error"); }
    }

    public void Stop() => _cts.Cancel();
    public void Dispose() { Stop(); _watcher?.Dispose(); _cts.Dispose(); }
}
