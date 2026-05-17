using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace PeripheralMonitor.Agent;

// ── Event payload ────────────────────────────────────────────────────────
public record DeviceInfo(
    string DeviceId,
    string FriendlyName,
    string Category,
    string Status
);

// ── Three-layer enterprise device monitor ────────────────────────────────
// Layer 1: RegisterDeviceNotification via service handle  (real-time, Session 0)
// Layer 2: SetupAPI snapshot comparison                   (reliable enumeration)
// Layer 3: ManagementEventWatcher (WMI)                   (fallback + reconciliation)
public sealed class DeviceMonitorEngine : IDisposable
{
    private readonly ILogger                       _logger;
    private readonly CancellationTokenSource       _cts = new();

    // Layer 1 — Win32 service notification
    //private IntPtr                                 _devNotifyHandle = IntPtr.Zero;
    //private ServiceBase?                           _serviceBase;

    // Layer 2 — SetupAPI snapshot
    private ConcurrentDictionary<string, DeviceInfo> _snapshot = new();
    private readonly SemaphoreSlim                 _snapshotLock = new(1, 1);

    // Layer 3 — WMI watcher
    private ManagementEventWatcher?                _wmiWatcher;

    // De-duplication: suppress duplicate events within 2 seconds
    private readonly ConcurrentDictionary<string, DateTime> _recentEvents = new();
    private const int DedupWindowSeconds = 2;

    public event Func<string, DeviceInfo, Task>?   DeviceChanged; // (eventType, device)

    // ── Win32 Constants ──────────────────────────────────────────────────
    private const int SERVICE_CONTROL_DEVICEEVENT        = 0x0000000B;
    private const int DBT_DEVICEARRIVAL                  = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE           = 0x8004;
    private const int DBT_DEVTYP_DEVICEINTERFACE         = 5;
    private const int DEVICE_NOTIFY_SERVICE_HANDLE       = 1;
    private const int DEVICE_NOTIFY_ALL_INTERFACE_CLASSES = 4;
    private const int DIGCF_PRESENT                      = 0x00000002;
    private const int DIGCF_ALLCLASSES                   = 0x00000004;
    private const int SPDRP_DEVICEDESC                   = 0x00000000;
    private const int SPDRP_FRIENDLYNAME                 = 0x0000000C;
    private const int SPDRP_CLASS                        = 0x00000007;

    // ── Win32 Structs ────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int    dbcc_size;
        public int    dbcc_devicetype;
        public int    dbcc_reserved;
        public Guid   dbcc_classguid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string dbcc_name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int    cbSize;
        public Guid   ClassGuid;
        public int    DevInst;
        public IntPtr Reserved;
    }

    // ── Win32 Imports ────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(
        IntPtr hRecipient, IntPtr NotificationFilter, int Flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterDeviceNotification(IntPtr Handle);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, int Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet, int MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        int Property, out int PropertyRegDataType,
        System.Text.StringBuilder PropertyBuffer, int PropertyBufferSize, out int RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        System.Text.StringBuilder DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

    // ── Constructor ──────────────────────────────────────────────────────
    public DeviceMonitorEngine(ILogger logger)
    {
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════════════
    // PUBLIC: Start all three layers
    // ════════════════════════════════════════════════════════════════════
    public async Task StartAsync()
    {
        // Take initial snapshot before starting watchers
        _snapshot = await TakeSnapshotAsync();
        _logger.LogInformation("Initial device snapshot: {Count} devices", _snapshot.Count);

        // Layer 2: SetupAPI snapshot reconciliation (every 30s)
        StartLayer2_SnapshotReconciliation();

        // Layer 3: WMI ManagementEventWatcher (real-time fallback)
        StartLayer3_WmiWatcher();

        _logger.LogInformation("DeviceMonitorEngine started — Layers 2+3 active (SetupAPI + WMI)");
  }

    public void Stop()
    {
        _cts.Cancel();
        //StopLayer1();
        StopLayer3();
        _logger.LogInformation("DeviceMonitorEngine stopped");
    }

    // ════════════════════════════════════════════════════════════════════
    // LAYER 1 — RegisterDeviceNotification via service handle
    //           Works in Session 0, real-time, Windows XP+
    // ════════════════════════════════════════════════════════════════════
   /* private void StartLayer1_ServiceNotification()
    {
        if (_serviceBase == null)
        {
            _logger.LogWarning("Layer 1: No ServiceBase provided — skipping Win32 service notification");
            return;
        }

        try
        {
            var filter = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size       = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_classguid  = Guid.Empty
            };

            var filterPtr = Marshal.AllocHGlobal(filter.dbcc_size);
            Marshal.StructureToPtr(filter, filterPtr, false);

            _devNotifyHandle = RegisterDeviceNotification(
                _serviceBase.ServiceHandle,
                filterPtr,
                DEVICE_NOTIFY_SERVICE_HANDLE | DEVICE_NOTIFY_ALL_INTERFACE_CLASSES);

            Marshal.FreeHGlobal(filterPtr);

            if (_devNotifyHandle == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning("Layer 1: RegisterDeviceNotification failed (error {Error}) — relying on Layers 2+3", err);
            }
            else
            {
                _logger.LogInformation("Layer 1: RegisterDeviceNotification active (service handle)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Layer 1: Failed to register device notification");
        }
    }

    private void StopLayer1()
    {
        if (_devNotifyHandle != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_devNotifyHandle);
            _devNotifyHandle = IntPtr.Zero;
        }
    }

    // Called by ServiceBase.OnCustomCommand when SERVICE_CONTROL_DEVICEEVENT arrives
    public void HandleServiceDeviceEvent(int eventType, IntPtr eventData)
    {
        try
        {
            if (eventType != DBT_DEVICEARRIVAL && eventType != DBT_DEVICEREMOVECOMPLETE)
                return;

            if (eventData == IntPtr.Zero) return;

            var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(eventData);
            if (hdr.dbch_devicetype != DBT_DEVTYP_DEVICEINTERFACE) return;

            var info     = Marshal.PtrToStructure<DEV_BROADCAST_DEVICEINTERFACE>(eventData);
            var deviceId = info.dbcc_name ?? "";
            var action   = eventType == DBT_DEVICEARRIVAL ? "Connected" : "Disconnected";

            _logger.LogDebug("Layer 1 event: {Action} {DeviceId}", action, deviceId);

            // Enrich with SetupAPI and fire
            Task.Run(async () =>
            {
                var device = await EnrichDeviceAsync(deviceId, action);
                await FireEventAsync(action, device);

                // Update snapshot after Layer 1 event
                await RefreshSnapshotAsync();
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Layer 1: HandleServiceDeviceEvent error");
        }
    }*/

    // ════════════════════════════════════════════════════════════════════
    // LAYER 2 — SetupAPI snapshot comparison
    //           Reliable enumeration, catches anything Layer 1 misses
    //           Polls every 30 seconds
    // ════════════════════════════════════════════════════════════════════
    private void StartLayer2_SnapshotReconciliation()
    {
        Task.Run(async () =>
        {
            _logger.LogInformation("Layer 2: Snapshot reconciliation active (30s interval)");

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                    await RefreshSnapshotAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Layer 2: Snapshot reconciliation error");
                    await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                }
            }
        }, _cts.Token);
    }

    private async Task RefreshSnapshotAsync()
    {
        if (!await _snapshotLock.WaitAsync(TimeSpan.FromSeconds(5)))
            return;

        try
        {
            var newSnapshot = await TakeSnapshotAsync();

            // Devices in new snapshot but not in old = Connected
            var arrived = newSnapshot.Keys.Except(_snapshot.Keys).ToList();
            foreach (var id in arrived)
            {
                var device = newSnapshot[id];
                _logger.LogInformation("Layer 2 detected arrival: {Name}", device.FriendlyName);
                await FireEventAsync("Connected", device);
            }

            // Devices in old snapshot but not in new = Disconnected
            var removed = _snapshot.Keys.Except(newSnapshot.Keys).ToList();
            foreach (var id in removed)
            {
                var device = _snapshot[id];
                _logger.LogInformation("Layer 2 detected removal: {Name}", device.FriendlyName);
                await FireEventAsync("Disconnected", device);
            }

            _snapshot = newSnapshot;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    // ── SetupAPI: enumerate ALL present devices ──────────────────────────
    private Task<ConcurrentDictionary<string, DeviceInfo>> TakeSnapshotAsync()
    {
        return Task.Run(() =>
        {
            var result  = new ConcurrentDictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
            var hDevInfo = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero,
                               DIGCF_PRESENT | DIGCF_ALLCLASSES);

            if (hDevInfo == INVALID_HANDLE)
            {
                _logger.LogWarning("Layer 2: SetupDiGetClassDevs failed (error {Error})",
                    Marshal.GetLastWin32Error());
                return result;
            }

            try
            {
                var devInfoData = new SP_DEVINFO_DATA
                {
                    cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>()
                };

                for (int i = 0; SetupDiEnumDeviceInfo(hDevInfo, i, ref devInfoData); i++)
                {
                    try
                    {
                        // Get device instance ID
                        var idBuf = new System.Text.StringBuilder(512);
                        if (!SetupDiGetDeviceInstanceId(hDevInfo, ref devInfoData, idBuf, idBuf.Capacity, out _))
                            continue;

                        var deviceId = idBuf.ToString();

                        // Get friendly name (fall back to DeviceDesc)
                        var name = GetSetupApiProperty(hDevInfo, ref devInfoData, SPDRP_FRIENDLYNAME)
                                ?? GetSetupApiProperty(hDevInfo, ref devInfoData, SPDRP_DEVICEDESC)
                                ?? "Unknown Device";

                        // Get class/category
                        var category = GetSetupApiProperty(hDevInfo, ref devInfoData, SPDRP_CLASS)
                                    ?? "Other";

                        var device = new DeviceInfo(
                            DeviceId:     deviceId,
                            FriendlyName: name,
                            Category:     MapCategory(category),
                            Status:       "OK"
                        );

                        result[deviceId] = device;
                    }
                    catch { /* skip malformed entries */ }

                    devInfoData.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }

            return result;
        });
    }

    private static string? GetSetupApiProperty(IntPtr hDevInfo, ref SP_DEVINFO_DATA devInfoData, int property)
    {
        var buf = new System.Text.StringBuilder(512);
        if (SetupDiGetDeviceRegistryProperty(
                hDevInfo, ref devInfoData, property,
                out _, buf, buf.Capacity, out _))
        {
            var val = buf.ToString().Trim();
            return string.IsNullOrEmpty(val) ? null : val;
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════
    // LAYER 3 — WMI ManagementEventWatcher
    //           Fallback for any events Layers 1+2 miss
    //           Also catches device property changes (driver installs etc.)
    // ════════════════════════════════════════════════════════════════════
    private void StartLayer3_WmiWatcher()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2");
            scope.Options.Timeout = TimeSpan.FromSeconds(10);

            try { scope.Connect(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Layer 3: WMI scope connect failed — Layer 3 disabled");
                return;
            }

            var query = new WqlEventQuery(
                "__InstanceOperationEvent",
                TimeSpan.FromSeconds(2),
                "TargetInstance ISA 'Win32_PnPEntity'");

            _wmiWatcher = new ManagementEventWatcher(scope, query);
            _wmiWatcher.Options.Timeout = TimeSpan.FromSeconds(20);
            _wmiWatcher.EventArrived   += OnWmiEvent;
            _wmiWatcher.Start();

            _logger.LogInformation("Layer 3: WMI ManagementEventWatcher active");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Layer 3: WMI watcher failed to start — continuing without it");
        }
    }

    private void StopLayer3()
    {
        try
        {
            _wmiWatcher?.Stop();
            _wmiWatcher?.Dispose();
            _wmiWatcher = null;
        }
        catch { }
    }

    private async void OnWmiEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var eventClass = e.NewEvent.ClassPath.ClassName;
            string action = eventClass switch
            {
                "__InstanceCreationEvent" => "Connected",
                "__InstanceDeletionEvent" => "Disconnected",
                _                         => ""
            };

            if (string.IsNullOrEmpty(action)) return;

            var target     = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var deviceId   = target["DeviceID"]?.ToString()   ?? "";
            var name       = target["Name"]?.ToString()
                          ?? target["Caption"]?.ToString()
                          ?? "Unknown Device";
            var pnpClass   = target["PNPClass"]?.ToString()   ?? "Other";

            // Skip system/non-peripheral classes
            if (ShouldSkipClass(pnpClass)) return;

            var device = new DeviceInfo(
                DeviceId:     deviceId,
                FriendlyName: name,
                Category:     MapCategory(pnpClass),
                Status:       target["Status"]?.ToString() ?? "OK"
            );

            _logger.LogDebug("Layer 3 WMI event: {Action} {Name}", action, name);
            await FireEventAsync(action, device);

            // Sync snapshot after WMI event
            await RefreshSnapshotAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Layer 3: WMI event handler error");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // SHARED: Event firing with de-duplication
    // ════════════════════════════════════════════════════════════════════
    private async Task FireEventAsync(string action, DeviceInfo device)
    {
        // De-duplicate: same device + same action within DedupWindowSeconds = skip
        var key = $"{action}:{device.DeviceId}";
        var now = DateTime.UtcNow;

        if (_recentEvents.TryGetValue(key, out var lastFired))
        {
            if ((now - lastFired).TotalSeconds < DedupWindowSeconds)
            {
                _logger.LogDebug("Deduplicated event: {Action} {Name}", action, device.FriendlyName);
                return;
            }
        }

        _recentEvents[key] = now;

        // Clean old entries
        foreach (var old in _recentEvents.Where(kv => (now - kv.Value).TotalSeconds > 60).ToList())
            _recentEvents.TryRemove(old.Key, out _);

        try
        {
            if (DeviceChanged != null)
                await DeviceChanged.Invoke(action, device);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeviceChanged handler error");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════
    private async Task<DeviceInfo> EnrichDeviceAsync(string deviceId, string action)
    {
        // Try to find the device in current snapshot
        if (_snapshot.TryGetValue(deviceId, out var cached))
            return cached;

        // Try registry lookup
        var name     = GetRegistryFriendlyName(deviceId);
        var category = GuessCategory(deviceId);

        return new DeviceInfo(
            DeviceId:     deviceId,
            FriendlyName: name,
            Category:     category,
            Status:       action == "Connected" ? "OK" : "Removed"
        );
    }

    private static string GetRegistryFriendlyName(string deviceId)
    {
        try
        {
            if (string.IsNullOrEmpty(deviceId)) return "Unknown Device";

            var parts = deviceId.TrimStart('\\', '?', '.').TrimStart('\\').Split('#');
            if (parts.Length >= 3)
            {
                var regPath = $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}";
                using var key = Registry.LocalMachine.OpenSubKey(regPath);
                if (key != null)
                {
                    var friendly = key.GetValue("FriendlyName") as string;
                    if (!string.IsNullOrEmpty(friendly)) return friendly;

                    var desc = key.GetValue("DeviceDesc") as string;
                    if (!string.IsNullOrEmpty(desc))
                    {
                        var idx = desc.LastIndexOf(';');
                        return idx >= 0 ? desc[(idx + 1)..] : desc;
                    }
                }
            }
        }
        catch { }

        return GuessCategory(deviceId);
    }

    private static string GuessCategory(string deviceId)
    {
        var lower = deviceId.ToLowerInvariant();
        if (lower.Contains("hid"))       return "HID Device";
        if (lower.Contains("usb"))       return "USB Device";
        if (lower.Contains("bthenum"))   return "Bluetooth Device";
        if (lower.Contains("storage"))   return "Storage Device";
        if (lower.Contains("display"))   return "Display Device";
        if (lower.Contains("audio"))     return "Audio Device";
        if (lower.Contains("keyboard"))  return "Keyboard";
        if (lower.Contains("mouse"))     return "Mouse";
        return "Unknown Device";
    }

    private static string MapCategory(string rawClass)
    {
        return rawClass.ToLowerInvariant() switch
        {
            "keyboard"          => "⌨️ Keyboard",
            "mouse"             => "🖱️ Mouse",
            "hid"               => "🎮 HID Device",
            "usb"               => "🔌 USB Device",
            "diskdrive"         => "💾 Storage",
            "cdrom"             => "💿 Optical Drive",
            "monitor"           => "🖥️ Display",
            "display"           => "🖥️ Display",
            "audioendpoint"     => "🔊 Audio",
            "media"             => "🔊 Audio",
            "bluetooth"         => "📶 Bluetooth",
            "net"               => "🌐 Network",
            "camera"            => "📷 Camera",
            "image"             => "📷 Camera/Scanner",
            "printer"           => "🖨️ Printer",
            "ports"             => "🔌 COM/LPT Port",
            _                   => $"🔧 {rawClass}"
        };
    }

    private static bool ShouldSkipClass(string pnpClass)
    {
        var skip = new[] { "Computer", "Processor", "System", "Volume", "DiskDrive" };
        return skip.Any(s => string.Equals(s, pnpClass, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
        _snapshotLock.Dispose();
    }
}
