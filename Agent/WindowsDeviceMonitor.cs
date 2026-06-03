using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent;

[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceMonitor : IDeviceMonitor
{
    private readonly ILogger<WindowsDeviceMonitor> _log;
    private readonly CancellationTokenSource _cts = new();
    private ConcurrentDictionary<string, DeviceInfo> _snap = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ManagementEventWatcher? _wmi;
    private readonly ConcurrentDictionary<string, DateTime> _dedup = new();

    private const int DIGCF_PRESENT = 0x02, DIGCF_ALLCLASSES = 0x04;
    private const int SPDRP_DEVICEDESC = 0x00, SPDRP_FRIENDLYNAME = 0x0C, SPDRP_CLASS = 0x07;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(IntPtr g, string? e, IntPtr h, int f);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr s, int i, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)] static extern bool SetupDiGetDeviceRegistryProperty(IntPtr s, ref SP_DEVINFO_DATA d, int p, out int t, System.Text.StringBuilder b, int sz, out int r);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)] static extern bool SetupDiGetDeviceInstanceId(IntPtr s, ref SP_DEVINFO_DATA d, System.Text.StringBuilder b, int sz, out int r);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    private static readonly IntPtr INVALID = new(-1);

    public event Func<string, DeviceInfo, Task>? DeviceChanged;
    public WindowsDeviceMonitor(ILogger<WindowsDeviceMonitor> log) => _log = log;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _snap = await SnapAsync();
        _log.LogInformation("Windows monitor: {N} devices", _snap.Count);
        _ = Task.Run(PollLoopAsync, _cts.Token);
        StartWmi();
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetSnapshotAsync() => (await SnapAsync()).Values.ToList();

    private async Task PollLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try { await Task.Delay(30_000, _cts.Token); await RefreshAsync(); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Poll error"); }
        }
    }

    private async Task RefreshAsync()
    {
        if (!await _lock.WaitAsync(5000)) return;
        try
        {
            var next = await SnapAsync();
            foreach (var id in next.Keys.Except(_snap.Keys)) await FireAsync("Connected",    next[id]);
            foreach (var id in _snap.Keys.Except(next.Keys)) await FireAsync("Disconnected", _snap[id]);
            _snap = next;
        }
        finally { _lock.Release(); }
    }

    private Task<ConcurrentDictionary<string, DeviceInfo>> SnapAsync() => Task.Run(() =>
    {
        var result = new ConcurrentDictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
        var h = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (h == INVALID) return result;
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInfo(h, i, ref d); i++)
            {
                try
                {
                    var buf = new System.Text.StringBuilder(512);
                    if (!SetupDiGetDeviceInstanceId(h, ref d, buf, buf.Capacity, out _)) continue;
                    var id   = buf.ToString();
                    var name = Prop(h, ref d, SPDRP_FRIENDLYNAME) ?? Prop(h, ref d, SPDRP_DEVICEDESC) ?? "Unknown";
                    result[id] = new DeviceInfo(id, name, MapCat(Prop(h, ref d, SPDRP_CLASS) ?? "Other"), "OK");
                }
                catch { }
                d.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();
            }
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
        return result;
    });

    private static string? Prop(IntPtr h, ref SP_DEVINFO_DATA d, int p)
    {
        var b = new System.Text.StringBuilder(512);
        return SetupDiGetDeviceRegistryProperty(h, ref d, p, out _, b, b.Capacity, out _) && b.Length > 0 ? b.ToString().Trim() : null;
    }

    private void StartWmi()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2"); scope.Options.Timeout = TimeSpan.FromSeconds(10); scope.Connect();
            _wmi = new ManagementEventWatcher(scope, new WqlEventQuery("__InstanceOperationEvent", TimeSpan.FromSeconds(2), "TargetInstance ISA 'Win32_PnPEntity'"));
            _wmi.Options.Timeout = TimeSpan.FromSeconds(20);
            _wmi.EventArrived += async (_, e) =>
            {
                try
                {
                    var action = e.NewEvent.ClassPath.ClassName switch { "__InstanceCreationEvent" => "Connected", "__InstanceDeletionEvent" => "Disconnected", _ => "" };
                    if (string.IsNullOrEmpty(action)) return;
                    var t = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                    var cls = t["PNPClass"]?.ToString() ?? "Other";
                    if (Skip(cls)) return;
                    await FireAsync(action, new DeviceInfo(t["DeviceID"]?.ToString() ?? "", t["Name"]?.ToString() ?? "Unknown", MapCat(cls), t["Status"]?.ToString() ?? "OK"));
                    await RefreshAsync();
                }
                catch { }
            };
            _wmi.Start(); _log.LogInformation("WMI watcher active");
        }
        catch (Exception ex) { _log.LogWarning(ex, "WMI watcher failed"); }
    }

    private async Task FireAsync(string action, DeviceInfo dev)
    {
        var key = $"{action}:{dev.DeviceId}"; var now = DateTime.UtcNow;
        if (_dedup.TryGetValue(key, out var last) && (now - last).TotalSeconds < 2) return;
        _dedup[key] = now;
        try { if (DeviceChanged != null) await DeviceChanged(action, dev); } catch (Exception ex) { _log.LogWarning(ex, "DeviceChanged error"); }
    }

    private static string MapCat(string r) => r.ToLowerInvariant() switch
    {
        "keyboard" => "Keyboard", "mouse" => "Mouse", "hid" => "HID Device", "usb" => "USB Device",
        "diskdrive" => "Storage", "cdrom" => "Optical Drive", "monitor" or "display" => "Display",
        "audioendpoint" or "media" => "Audio", "bluetooth" => "Bluetooth", "net" => "Network",
        "camera" or "image" => "Camera", "printer" => "Printer", "ports" => "Serial/COM Port", _ => r
    };

    private static bool Skip(string c) => new[] { "Computer", "Processor", "System", "Volume" }
        .Any(s => s.Equals(c, StringComparison.OrdinalIgnoreCase));

    public void Stop() => _cts.Cancel();
    public void Dispose() { Stop(); _wmi?.Stop(); _wmi?.Dispose(); _cts.Dispose(); _lock.Dispose(); }
}
