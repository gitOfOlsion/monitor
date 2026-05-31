using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PeripheralMonitor.Agent;

/// <summary>
/// Windows implementation: Layer 2 (SetupAPI snapshot) + Layer 3 (WMI events).
/// Layer 1 (RegisterDeviceNotification via service handle) is available but disabled
/// until the agent is installed as a proper Windows Service.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceMonitor : IDeviceMonitor
{
    private readonly ILogger<WindowsDeviceMonitor>              _logger;
    private readonly CancellationTokenSource                    _cts = new();
    private ConcurrentDictionary<string, DeviceInfo>            _snapshot = new();
    private readonly SemaphoreSlim                              _snapshotLock = new(1, 1);
    private ManagementEventWatcher?                             _wmiWatcher;
    private readonly ConcurrentDictionary<string, DateTime>     _recentEvents = new();
    private const int DedupWindowSeconds = 2;

    // ── Win32 Constants & Imports ────────────────────────────────────────
    private const int DIGCF_PRESENT   = 0x00000002;
    private const int DIGCF_ALLCLASSES = 0x00000004;
    private const int SPDRP_DEVICEDESC  = 0x00000000;
    private const int SPDRP_FRIENDLYNAME = 0x0000000C;
    private const int SPDRP_CLASS       = 0x00000007;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int  cbSize;
        public Guid ClassGuid;
        public int  DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, int Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, int MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        int Property, out int PropertyRegDataType, System.Text.StringBuilder PropertyBuffer, int PropertyBufferSize, out int RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        System.Text.StringBuilder DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    private static readonly IntPtr INVALID_HANDLE = new(-1);

    public event Func<string, DeviceInfo, Task>? DeviceChanged;

    public WindowsDeviceMonitor(ILogger<WindowsDeviceMonitor> logger) => _logger = logger;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _snapshot = await TakeSnapshotAsync();
        _logger.LogInformation("Windows monitor: initial snapshot {Count} devices", _snapshot.Count);

        StartSnapshotReconciliation();
        StartWmiWatcher();

        _logger.LogInformation("WindowsDeviceMonitor started (SetupAPI + WMI)");
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetSnapshotAsync()
    {
        var snap = await TakeSnapshotAsync();
        return snap.Values.ToList();
    }

    // ── Layer 2: SetupAPI polling ────────────────────────────────────────
    private void StartSnapshotReconciliation()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                    await RefreshSnapshotAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Snapshot reconciliation error"); }
            }
        }, _cts.Token);
    }

    private async Task RefreshSnapshotAsync()
    {
        if (!await _snapshotLock.WaitAsync(TimeSpan.FromSeconds(5))) return;
        try
        {
            var next = await TakeSnapshotAsync();
            foreach (var id in next.Keys.Except(_snapshot.Keys))
                await FireEventAsync("Connected", next[id]);
            foreach (var id in _snapshot.Keys.Except(next.Keys))
                await FireEventAsync("Disconnected", _snapshot[id]);
            _snapshot = next;
        }
        finally { _snapshotLock.Release(); }
    }

    private Task<ConcurrentDictionary<string, DeviceInfo>> TakeSnapshotAsync() => Task.Run(() =>
    {
        var result    = new ConcurrentDictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
        var hDevInfo  = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (hDevInfo == INVALID_HANDLE) return result;
        try
        {
            var devInfoData = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInfo(hDevInfo, i, ref devInfoData); i++)
            {
                try
                {
                    var idBuf = new System.Text.StringBuilder(512);
                    if (!SetupDiGetDeviceInstanceId(hDevInfo, ref devInfoData, idBuf, idBuf.Capacity, out _)) continue;
                    var deviceId  = idBuf.ToString();
                    var name      = GetProp(hDevInfo, ref devInfoData, SPDRP_FRIENDLYNAME)
                                 ?? GetProp(hDevInfo, ref devInfoData, SPDRP_DEVICEDESC)
                                 ?? "Unknown Device";
                    var category  = GetProp(hDevInfo, ref devInfoData, SPDRP_CLASS) ?? "Other";
                    result[deviceId] = new DeviceInfo(deviceId, name, MapCategory(category), "OK");
                }
                catch { /* skip malformed */ }
                devInfoData.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();
            }
        }
        finally { SetupDiDestroyDeviceInfoList(hDevInfo); }
        return result;
    });

    private static string? GetProp(IntPtr hDevInfo, ref SP_DEVINFO_DATA data, int prop)
    {
        var buf = new System.Text.StringBuilder(512);
        return SetupDiGetDeviceRegistryProperty(hDevInfo, ref data, prop, out _, buf, buf.Capacity, out _)
            ? (buf.ToString().Trim() is { Length: > 0 } s ? s : null)
            : null;
    }

    // ── Layer 3: WMI ────────────────────────────────────────────────────
    private void StartWmiWatcher()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2");
            scope.Options.Timeout = TimeSpan.FromSeconds(10);
            scope.Connect();

            var query = new WqlEventQuery("__InstanceOperationEvent",
                TimeSpan.FromSeconds(2), "TargetInstance ISA 'Win32_PnPEntity'");

            _wmiWatcher = new ManagementEventWatcher(scope, query);
            _wmiWatcher.Options.Timeout = TimeSpan.FromSeconds(20);
            _wmiWatcher.EventArrived   += OnWmiEvent;
            _wmiWatcher.Start();
            _logger.LogInformation("WMI watcher active");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "WMI watcher failed to start"); }
    }

    private async void OnWmiEvent(object sender, EventArrivedEventArgs e)
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

            var target   = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var pnpClass = target["PNPClass"]?.ToString() ?? "Other";
            if (ShouldSkip(pnpClass)) return;

            var device = new DeviceInfo(
                DeviceId:     target["DeviceID"]?.ToString() ?? "",
                FriendlyName: target["Name"]?.ToString() ?? target["Caption"]?.ToString() ?? "Unknown Device",
                Category:     MapCategory(pnpClass),
                Status:       target["Status"]?.ToString() ?? "OK"
            );

            await FireEventAsync(action, device);
            await RefreshSnapshotAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "WMI event handler error"); }
    }

    // ── Shared helpers ───────────────────────────────────────────────────
    private async Task FireEventAsync(string action, DeviceInfo device)
    {
        var key = $"{action}:{device.DeviceId}";
        var now = DateTime.UtcNow;
        if (_recentEvents.TryGetValue(key, out var last) && (now - last).TotalSeconds < DedupWindowSeconds) return;
        _recentEvents[key] = now;
        foreach (var old in _recentEvents.Where(kv => (now - kv.Value).TotalSeconds > 60).ToList())
            _recentEvents.TryRemove(old.Key, out _);
        try { if (DeviceChanged != null) await DeviceChanged(action, device); }
        catch (Exception ex) { _logger.LogWarning(ex, "DeviceChanged handler error"); }
    }

    private static string MapCategory(string raw) => raw.ToLowerInvariant() switch
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
        "image"         => "Camera/Scanner",
        "printer"       => "Printer",
        "ports"         => "Serial/Parallel Port",
        _               => raw
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
