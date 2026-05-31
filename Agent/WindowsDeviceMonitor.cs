using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PeripheralMonitor.Agent;

[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceMonitor : IDeviceMonitor
{
    private readonly ILogger<WindowsDeviceMonitor>               _logger;
    private readonly CancellationTokenSource                     _cts = new();
    private ConcurrentDictionary<string, DeviceInfo>             _snapshot = new();
    private readonly SemaphoreSlim                               _snapshotLock = new(1, 1);
    private ManagementEventWatcher?                              _wmiWatcher;
    private readonly ConcurrentDictionary<string, DateTime>      _recentEvents = new();
    private const int DedupWindowSeconds = 2;

    private const int DIGCF_PRESENT    = 0x00000002;
    private const int DIGCF_ALLCLASSES = 0x00000004;
    private const int SPDRP_DEVICEDESC  = 0;
    private const int SPDRP_FRIENDLYNAME = 12;
    private const int SPDRP_CLASS       = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr g, string? e, IntPtr h, int f);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr s, int i, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr s, ref SP_DEVINFO_DATA d, int p, out int t, System.Text.StringBuilder b, int sz, out int r);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr s, ref SP_DEVINFO_DATA d, System.Text.StringBuilder id, int sz, out int r);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    private static readonly IntPtr INVALID_HANDLE = new(-1);

    public event Func<string, DeviceInfo, Task>? DeviceChanged;

    public WindowsDeviceMonitor(ILogger<WindowsDeviceMonitor> logger) => _logger = logger;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _snapshot = await TakeSnapshotAsync();
        _logger.LogInformation("Windows monitor: {Count} devices in initial snapshot", _snapshot.Count);
        StartSnapshotPoller();
        StartWmiWatcher();
    }

    public Task<IReadOnlyList<DeviceInfo>> GetSnapshotAsync()
        => Task.FromResult<IReadOnlyList<DeviceInfo>>(_snapshot.Values.ToList());

    private void StartSnapshotPoller()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token); await RefreshAsync(); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Snapshot poll error"); }
            }
        });
    }

    private async Task RefreshAsync()
    {
        if (!await _snapshotLock.WaitAsync(TimeSpan.FromSeconds(5))) return;
        try
        {
            var next = await TakeSnapshotAsync();
            foreach (var id in next.Keys.Except(_snapshot.Keys).ToList())
                await FireAsync("Connected",    next[id]);
            foreach (var id in _snapshot.Keys.Except(next.Keys).ToList())
                await FireAsync("Disconnected", _snapshot[id]);
            _snapshot = next;
        }
        finally { _snapshotLock.Release(); }
    }

    private Task<ConcurrentDictionary<string, DeviceInfo>> TakeSnapshotAsync() => Task.Run(() =>
    {
        var result   = new ConcurrentDictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
        var hDevInfo = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (hDevInfo == INVALID_HANDLE) return result;
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInfo(hDevInfo, i, ref d); i++)
            {
                try
                {
                    var idBuf = new System.Text.StringBuilder(512);
                    if (!SetupDiGetDeviceInstanceId(hDevInfo, ref d, idBuf, idBuf.Capacity, out _)) continue;
                    var deviceId = idBuf.ToString();
                    var name     = GetProp(hDevInfo, ref d, SPDRP_FRIENDLYNAME)
                                ?? GetProp(hDevInfo, ref d, SPDRP_DEVICEDESC) ?? "Unknown Device";
                    var cls      = GetProp(hDevInfo, ref d, SPDRP_CLASS) ?? "Other";
                    result[deviceId] = new DeviceInfo(deviceId, name, MapCategory(cls), "OK");
                }
                catch { }
                d.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();
            }
        }
        finally { SetupDiDestroyDeviceInfoList(hDevInfo); }
        return result;
    });

    private static string? GetProp(IntPtr h, ref SP_DEVINFO_DATA d, int p)
    {
        var b = new System.Text.StringBuilder(512);
        return SetupDiGetDeviceRegistryProperty(h, ref d, p, out _, b, b.Capacity, out _)
            ? b.ToString().Trim() is { Length: > 0 } s ? s : null : null;
    }

    private void StartWmiWatcher()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2");
            scope.Options.Timeout = TimeSpan.FromSeconds(10);
            scope.Connect();
            var query = new WqlEventQuery("__InstanceOperationEvent", TimeSpan.FromSeconds(2),
                "TargetInstance ISA 'Win32_PnPEntity'");
            _wmiWatcher = new ManagementEventWatcher(scope, query);
            _wmiWatcher.Options.Timeout = TimeSpan.FromSeconds(20);
            _wmiWatcher.EventArrived   += OnWmi;
            _wmiWatcher.Start();
            _logger.LogInformation("WMI watcher active");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "WMI watcher failed"); }
    }

    private async void OnWmi(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var action = e.NewEvent.ClassPath.ClassName switch
            {
                "__InstanceCreationEvent" => "Connected",
                "__InstanceDeletionEvent" => "Disconnected",
                _ => ""
            };
            if (string.IsNullOrEmpty(action)) return;
            var t = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var pnpClass = t["PNPClass"]?.ToString() ?? "Other";
            if (ShouldSkip(pnpClass)) return;
            var device = new DeviceInfo(
                t["DeviceID"]?.ToString() ?? "",
                t["Name"]?.ToString() ?? t["Caption"]?.ToString() ?? "Unknown Device",
                MapCategory(pnpClass),
                t["Status"]?.ToString() ?? "OK");
            await FireAsync(action, device);
            await RefreshAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "WMI event error"); }
    }

    private async Task FireAsync(string action, DeviceInfo device)
    {
        var key = $"{action}:{device.DeviceId}";
        var now = DateTime.UtcNow;
        if (_recentEvents.TryGetValue(key, out var last) && (now - last).TotalSeconds < DedupWindowSeconds) return;
        _recentEvents[key] = now;
        foreach (var old in _recentEvents.Where(kv => (now - kv.Value).TotalSeconds > 60).ToList())
            _recentEvents.TryRemove(old.Key, out _);
        try { if (DeviceChanged != null) await DeviceChanged(action, device); }
        catch (Exception ex) { _logger.LogWarning(ex, "DeviceChanged error"); }
    }

    // Maps Windows class name → DeviceCategory.Name (matches DB seed)
    public static string MapCategory(string raw) => raw.ToLowerInvariant() switch
    {
        "keyboard"      => "Keyboard",
        "mouse"         => "Mouse",
        "hid"           => "HID Device",
        "usb"           => "USB Device",
        "diskdrive"     => "Storage",
        "cdrom"         => "Optical Drive",
        "monitor"       => "Display",
        "display"       => "Display",
        "audioendpoint" => "Audio",
        "media"         => "Audio",
        "bluetooth"     => "Bluetooth",
        "net"           => "Network",
        "camera"        => "Camera",
        "image"         => "Camera",
        "printer"       => "Printer",
        "ports"         => "Serial Port",
        _               => "Other"
    };

    private static bool ShouldSkip(string c) =>
        new[] { "Computer", "Processor", "System", "Volume" }
            .Any(s => string.Equals(s, c, StringComparison.OrdinalIgnoreCase));

    public void Stop() => _cts.Cancel();

    public void Dispose()
    {
        Stop();
        _wmiWatcher?.Stop();
        _wmiWatcher?.Dispose();
        _cts.Dispose();
        _snapshotLock.Dispose();
    }
}
