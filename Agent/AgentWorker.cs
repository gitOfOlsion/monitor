using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using PeripheralMonitor.Agent.Services;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent;

public class AgentWorker : BackgroundService
{
    private HubConnection?   _hub;
    private readonly IDeviceMonitor   _monitor;
    private readonly DiscoveryService _discovery;
    private readonly CommandExecutor  _commands;
    private readonly FileShareService _files;
    private readonly TunnelClient     _tunnel;
    private readonly IConfiguration   _config;
    private readonly ILogger<AgentWorker> _log;

    private readonly string        _machineName = Environment.MachineName;
    private readonly string        _macAddress  = NetHelpers.GetMac();
    private readonly AgentPlatform _platform    = OperatingSystem.IsWindows() ? AgentPlatform.Windows : AgentPlatform.Linux;
    private Guid _institutionId = Guid.Empty;

    // Durable, restart- and reinstall-safe event storage (JSONL outbox + history
    // in %ProgramData%). Replaces the old in-memory queue that lost events on restart.
    private readonly EventStore _store;
    private readonly AppControlEnforcer _appControl;

    public AgentWorker(IConfiguration c, IDeviceMonitor m, DiscoveryService d, CommandExecutor cmd, FileShareService files, TunnelClient tunnel, ILogger<AgentWorker> l)
    { _config = c; _monitor = m; _discovery = d; _commands = cmd; _files = files; _tunnel = tunnel; _log = l; _store = new EventStore(l); _appControl = new AppControlEnforcer(l); }

    /// <summary>Returns the currently logged-in interactive user, or null if none.</summary>
    // ── Win32 Terminal Services API (works from Session 0 services) ─────────
    [System.Runtime.InteropServices.DllImport("wtsapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int WTSGetActiveConsoleSessionId();

    // Diagnostic info from the last GetLoggedInUser call, logged in heartbeat
    private static string _userDiag = "";

    private static string? GetLoggedInUser()
    {
        if (!OperatingSystem.IsWindows()) return GetLoggedInUserLinux();

        // Probe session IDs directly with WTSQuerySessionInformation. Unlike
        // WTSEnumerateSessions (which returns ERROR_NO_MORE_ITEMS / 259 from a
        // Session 0 service), per-session queries work fine. We check the active
        // console session first, then scan a reasonable range of session IDs for
        // any session that has a logged-in username (covers RDP sessions too).
        const int WTSUserName = 5;
        const int WTSDomainName = 7;
        const int WTSWinStationName = 6;
        IntPtr server = IntPtr.Zero;

        var diag = new System.Text.StringBuilder();
        try
        {
            // Probe sessions 0..30 (covers high console session numbers from repeated RDP).
            int activeConsole = WTSGetActiveConsoleSessionId();
            var candidates = new List<int>();
            if (activeConsole >= 0 && activeConsole != 0xFFFFFFF) candidates.Add(activeConsole);
            for (int s = 0; s <= 30; s++) if (!candidates.Contains(s)) candidates.Add(s);

            foreach (var sid in candidates)
            {
                // Query username with explicit byte-count tracking
                bool ok = WTSQuerySessionInformation(server, sid, WTSUserName, out var buf, out var bytes);
                string? user = null;
                if (ok && bytes > 2)
                {
                    user = System.Runtime.InteropServices.Marshal.PtrToStringUni(buf);
                    WTSFreeMemory(buf);
                }
                else if (ok) { WTSFreeMemory(buf); }

                if (!string.IsNullOrWhiteSpace(user))
                {
                    var domain = QueryWtsString(server, sid, WTSDomainName);
                    _userDiag = $"FOUND console={activeConsole} sid={sid} user={user}";
                    return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
                }

                // For the active console session, capture detailed diagnostics
                if (sid == activeConsole)
                {
                    var win = QueryWtsString(server, sid, WTSWinStationName);
                    diag.Append($"console{sid}(ok={ok},bytes={bytes},win='{win}') ");
                }
            }
            _userDiag = $"NO USER. console={activeConsole}. {diag}";
        }
        catch (Exception ex) { _userDiag = $"WTS exception: {ex.Message}"; }

        // FALLBACK: query user (works if somehow running interactively)
        try
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var result = RunCommand(System.IO.Path.Combine(system32, "query.exe"), "user");
            if (!string.IsNullOrWhiteSpace(result))
                foreach (var line in result.Split('\n'))
                {
                    if (line.Contains("USERNAME", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Trim().TrimStart('>').Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0])) return parts[0];
                }
        }
        catch { }
        return null;
    }

    private static string? QueryWtsString(IntPtr server, int sessionId, int infoClass)
    {
        if (WTSQuerySessionInformation(server, sessionId, infoClass, out var buffer, out var bytes) && bytes > 2)
        {
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer); }
            finally { WTSFreeMemory(buffer); }
        }
        return null;
    }

    private static string RunCommand(string exe, string args)
    {
        try
        {
            if (!System.IO.File.Exists(exe)) return "";
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "";
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return output;
        }
        catch { return ""; }
    }

    private static string? GetLoggedInUserLinux()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("who")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                var line = output.Split('\n').FirstOrDefault(l => l.Contains(":0") || l.Contains("tty"));
                if (line != null) return line.Split(' ')[0];
            }
        }
        catch { }
        return null;
    }

    // Hardware info is collected once (doesn't change between heartbeats)
    private static (string? cpu, int? cores, long? ramMb, string? disk) _hwCache = (null, null, null, null);
    private static bool _hwCollected;

    private static (string? cpu, int? cores, long? ramMb, string? disk) GetHardwareInfo()
    {
        if (_hwCollected) return _hwCache;
        _hwCollected = true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var scope = new System.Management.ManagementScope(@"\\.\root\cimv2");
                scope.Connect();

                // CPU
                string? cpuName = null; int? cores = null;
                using var cpuQ = new System.Management.ManagementObjectSearcher(scope,
                    new System.Management.ObjectQuery("SELECT Name, NumberOfLogicalProcessors FROM Win32_Processor"));
                foreach (System.Management.ManagementObject o in cpuQ.Get())
                {
                    cpuName = o["Name"]?.ToString()?.Trim();
                    cores   = Convert.ToInt32(o["NumberOfLogicalProcessors"]);
                    break;
                }

                // RAM
                long? ramMb = null;
                using var ramQ = new System.Management.ManagementObjectSearcher(scope,
                    new System.Management.ObjectQuery("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"));
                foreach (System.Management.ManagementObject o in ramQ.Get())
                {
                    var bytes = Convert.ToInt64(o["TotalPhysicalMemory"]);
                    ramMb = bytes / (1024 * 1024);
                    break;
                }

                // Disks
                var disks = new List<string>();
                using var diskQ = new System.Management.ManagementObjectSearcher(scope,
                    new System.Management.ObjectQuery("SELECT Model, Size, MediaType FROM Win32_DiskDrive"));
                foreach (System.Management.ManagementObject o in diskQ.Get())
                {
                    var model     = o["Model"]?.ToString()?.Trim() ?? "Disk";
                    var sizeBytes = Convert.ToInt64(o["Size"] ?? 0L);
                    var sizeGb    = sizeBytes / (1024L * 1024 * 1024);
                    var media     = o["MediaType"]?.ToString() ?? "";
                    var kind      = media.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
                                  : media.Contains("NVM", StringComparison.OrdinalIgnoreCase) || model.Contains("NVM", StringComparison.OrdinalIgnoreCase) ? "NVMe"
                                  : "HDD";
                    disks.Add($"{kind} {sizeGb}GB");
                }

                _hwCache = (cpuName, cores, ramMb, disks.Count > 0 ? string.Join(", ", disks) : null);
            }
            else
            {
                // Linux: read from /proc
                string? cpuName = null; int? cores = null; long? ramMb = null;
                var cpuInfo = System.IO.File.Exists("/proc/cpuinfo") ? System.IO.File.ReadAllText("/proc/cpuinfo") : "";
                var modelLine = cpuInfo.Split('\n').FirstOrDefault(l => l.StartsWith("model name"));
                if (modelLine != null) cpuName = modelLine.Split(':').LastOrDefault()?.Trim();
                cores = cpuInfo.Split('\n').Count(l => l.StartsWith("processor"));
                if (cores == 0) cores = null;

                var memInfo = System.IO.File.Exists("/proc/meminfo") ? System.IO.File.ReadAllText("/proc/meminfo") : "";
                var memLine = memInfo.Split('\n').FirstOrDefault(l => l.StartsWith("MemTotal"));
                if (memLine != null && long.TryParse(new string(memLine.Where(char.IsDigit).ToArray()), out var kb))
                    ramMb = kb / 1024;

                _hwCache = (cpuName, cores, ramMb, null);
            }
        }
        catch { /* non-fatal, return whatever we got */ }
        return _hwCache;
    }

    // ── Live utilisation sampling ────────────────────────────────────────
    private (double? cpu, double? ramPct, double? diskPct, long? ramUsedMb, double? diskActive, double? netKbps) GetLiveMetrics()
    {
        double? cpu = null, ramPct = null, diskPct = null, diskActive = null, netKbps = null;
        long? ramUsedMb = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var m = WindowsLiveMetrics();
                cpu = m.cpu;
                diskActive = m.diskActive;
                netKbps = m.netKbps;
                if (m.ramTotal > 0)
                {
                    var used = m.ramTotal - m.ramAvail;
                    ramUsedMb = used / (1024 * 1024);
                    ramPct = 100.0 * used / m.ramTotal;
                }
            }
            else
            {
                var m = LinuxLiveMetrics();
                cpu = m.cpu;
                ramUsedMb = m.ramUsedMb;
                ramPct = m.ramTotalMb > 0 ? 100.0 * m.ramUsedMb / m.ramTotalMb : null;
                diskActive = m.diskActive;
                netKbps = m.netKbps;
            }

            // Disk SPACE used % (system drive)
            var sysRoot = OperatingSystem.IsWindows()
                ? Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\"
                : "/";
            var di = new DriveInfo(sysRoot);
            if (di.IsReady && di.TotalSize > 0)
                diskPct = 100.0 * (di.TotalSize - di.AvailableFreeSpace) / di.TotalSize;
        }
        catch { /* non-fatal */ }

        return (Round(cpu), Round(ramPct), Round(diskPct), ramUsedMb, Round(diskActive), Round(netKbps));
    }

    private static double? Round(double? v) => v.HasValue ? Math.Round(v.Value, 1) : null;

    // Returns accurate Task-Manager-equivalent metrics via WMI formatted perf data.
    private (double? cpu, long ramAvail, long ramTotal, double? diskActive, double? netKbps) WindowsLiveMetrics()
    {
        double? cpu = null, diskActive = null, netKbps = null;
        long ramAvail = 0, ramTotal = 0;
        try
        {
            var scope = new System.Management.ManagementScope(@"\\.\root\cimv2");
            scope.Connect();

            // CPU via two raw-counter samples 250ms apart. PercentProcessorTime on the
            // raw class is cumulative "100ns busy" units per processor; we use the
            // _Total instance's PercentProcessorTime + TimeStamp_Sys100NS pair and the
            // standard formula: busy% = 100 * (1 - idleDelta/totalDelta).
            // Simpler & robust: use PerfRawData PercentIdleTime is not present, so we
            // sample the formatted class TWICE — the second read returns a real value.
            double ReadFormattedCpu()
            {
                using var q = new System.Management.ManagementObjectSearcher(scope,
                    new System.Management.ObjectQuery(
                        "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"));
                foreach (System.Management.ManagementObject o in q.Get())
                    if (o["PercentProcessorTime"] != null)
                        return Convert.ToDouble(o["PercentProcessorTime"]);
                return -1;
            }
            // First read primes the counter; second (after a short wait) is accurate.
            ReadFormattedCpu();
            System.Threading.Thread.Sleep(300);
            var c = ReadFormattedCpu();
            if (c >= 0) cpu = c;

            // RAM (KB → bytes)
            using (var q = new System.Management.ManagementObjectSearcher(scope,
                new System.Management.ObjectQuery("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem")))
            {
                foreach (System.Management.ManagementObject o in q.Get())
                {
                    ramAvail = Convert.ToInt64(o["FreePhysicalMemory"]) * 1024;
                    ramTotal = Convert.ToInt64(o["TotalVisibleMemorySize"]) * 1024;
                    break;
                }
            }

            // Disk activity: formatted PercentDiskTime, sampled twice like CPU.
            double ReadDisk()
            {
                using var q = new System.Management.ManagementObjectSearcher(scope,
                    new System.Management.ObjectQuery(
                        "SELECT PercentDiskTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'"));
                foreach (System.Management.ManagementObject o in q.Get())
                    if (o["PercentDiskTime"] != null)
                        return Convert.ToDouble(o["PercentDiskTime"]);
                return -1;
            }
            var d = ReadDisk();
            if (d >= 0) diskActive = Math.Min(100.0, d);

            // Network: formatted BytesTotalPerSec, summed across interfaces (already a rate).
            using (var q = new System.Management.ManagementObjectSearcher(scope,
                new System.Management.ObjectQuery(
                    "SELECT BytesTotalPerSec FROM Win32_PerfFormattedData_Tcpip_NetworkInterface")))
            {
                double totalBytes = 0; bool any = false;
                foreach (System.Management.ManagementObject o in q.Get())
                    if (o["BytesTotalPerSec"] != null) { totalBytes += Convert.ToDouble(o["BytesTotalPerSec"]); any = true; }
                if (any) netKbps = totalBytes / 1024.0;
            }
        }
        catch { /* non-fatal */ }
        return (cpu, ramAvail, ramTotal, diskActive, netKbps);
    }

    // Linux live metrics including disk activity and network throughput
    private (double? cpu, long ramUsedMb, long ramTotalMb, double? diskActive, double? netKbps) LinuxLiveMetrics()
    {
        double? cpu = null, diskActive = null, netKbps = null;
        long ramUsedMb = 0, ramTotalMb = 0;
        try
        {
            if (File.Exists("/proc/meminfo"))
            {
                long total = 0, avail = 0;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:")) total = ParseMeminfoKb(line);
                    else if (line.StartsWith("MemAvailable:")) avail = ParseMeminfoKb(line);
                    if (total > 0 && avail > 0) break;
                }
                ramTotalMb = total / 1024;
                ramUsedMb  = (total - avail) / 1024;
            }

            cpu = SampleLinuxCpu();
            netKbps = SampleLinuxNet();
            // Disk activity on Linux requires parsing /proc/diskstats deltas; left null
            // for now (the GUI shows "—"). Could be added if Linux agents need it.
        }
        catch { /* non-fatal */ }
        return (cpu, ramUsedMb, ramTotalMb, diskActive, netKbps);
    }

    // Sample network throughput from /proc/net/dev over a short interval (KB/s)
    private static double? SampleLinuxNet()
    {
        try
        {
            long Read()
            {
                long total = 0;
                foreach (var line in File.ReadLines("/proc/net/dev"))
                {
                    var idx = line.IndexOf(':');
                    if (idx < 0) continue;
                    var iface = line[..idx].Trim();
                    if (iface == "lo") continue;
                    var parts = line[(idx + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 9)
                    {
                        long.TryParse(parts[0], out var rx);  // bytes received
                        long.TryParse(parts[8], out var tx);  // bytes transmitted
                        total += rx + tx;
                    }
                }
                return total;
            }
            var b1 = Read();
            System.Threading.Thread.Sleep(200);
            var b2 = Read();
            var bytesPerSec = (b2 - b1) * 5.0; // 200ms → ×5 for per-second
            return bytesPerSec / 1024.0;       // KB/s
        }
        catch { return null; }
    }

    private static long ParseMeminfoKb(string line)
    {
        var digits = new string(line.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var v) ? v : 0;
    }

    private static double? SampleLinuxCpu()
    {
        try
        {
            var (idle1, total1) = ReadProcStat();
            System.Threading.Thread.Sleep(200);
            var (idle2, total2) = ReadProcStat();
            var idleDelta = idle2 - idle1;
            var totalDelta = total2 - total1;
            if (totalDelta <= 0) return null;
            return Math.Round(100.0 * (1.0 - (double)idleDelta / totalDelta), 1);
        }
        catch { return null; }
    }

    private static (long idle, long total) ReadProcStat()
    {
        var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu "));
        if (line == null) return (0, 0);
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long total = 0; long idle = 0;
        for (int i = 1; i < parts.Length; i++)
        {
            if (long.TryParse(parts[i], out var v)) { total += v; if (i == 4) idle = v; }
        }
        return (idle, total);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Agent starting — {Machine} | {Mac} | {Platform}", _machineName, _macAddress, _platform);

        var token = await ResolveTokenAsync(ct);
        if (token == null)
        {
            _log.LogWarning("No JWT token found. Set PM_JwtToken env var or place appsettings.local.json");
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }

        _institutionId = ExtractInstitutionId(token);
        if (_institutionId == Guid.Empty)
        {
            _log.LogError("JWT missing institution_id claim — regenerate via dashboard");
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }

        _log.LogInformation("Institution ID: {Inst}", _institutionId);
        _discovery.StartBroadcasting(_machineName, _macAddress, _platform, _institutionId);
        BuildHub(token);
        await ConnectAsync(ct);

        _monitor.DeviceChanged += OnDeviceChanged;
        await _monitor.StartAsync(ct);
        await HeartbeatLoopAsync(ct);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_hub?.State == HubConnectionState.Connected)
                {
                    var snap = await _monitor.GetSnapshotAsync();
                    var loggedInUser = GetLoggedInUser();
                    var (cpu, cores, ramMb, disk) = GetHardwareInfo();
                    var (cpuPct, ramPct, diskPct, ramUsedMb, diskActive, netKbps) = GetLiveMetrics();
                    _log.LogInformation("Heartbeat — {N} devices, User={User}, CPU%={Cpu} RAM%={Ram} Disk%={Disk} Net={Net}KB/s",
                        snap.Count, loggedInUser ?? "none", cpuPct?.ToString() ?? "?", ramPct?.ToString() ?? "?",
                        diskActive?.ToString() ?? "?", netKbps?.ToString("F0") ?? "?");
                    await _hub.SendAsync("AgentHeartbeat",
                        new HeartbeatPayload(_machineName, _macAddress, _institutionId, _platform.ToString(),
                            DateTime.UtcNow, snap.Count, loggedInUser, cpu, cores, ramMb, disk,
                            cpuPct, ramPct, diskPct, ramUsedMb, diskActive, netKbps), ct);
                    await FlushPendingEventsAsync();
                }
                else if (_hub?.State == HubConnectionState.Disconnected)
                    try { await _hub.StartAsync(ct); } catch { }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Heartbeat error"); }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private async Task OnDeviceChanged(string eventType, DeviceInfo dev)
    {
        var payload = new DeviceChangedPayload(_machineName, _institutionId, eventType,
            dev.DeviceId, dev.FriendlyName, dev.Category, dev.Status, DateTime.UtcNow);

        // ALWAYS persist first (history + outbox). This guarantees the event survives
        // a crash/restart/reinstall even if it's never successfully sent.
        payload = _store.Record(payload);

        // Then try to deliver immediately if connected.
        if (_hub?.State == HubConnectionState.Connected)
        {
            try
            {
                await _hub.SendAsync("DeviceChanged", payload);
                _store.MarkDelivered(new[] { payload.EventId });
                _log.LogInformation("[{Event}] {Cat} — {Name}", eventType, dev.Category, dev.FriendlyName);
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DeviceChanged send failed - left in durable outbox for retry");
            }
        }
        else
        {
            _log.LogWarning("[{Event}] {Cat} — {Name} (offline, persisted to outbox)", eventType, dev.Category, dev.FriendlyName);
        }
    }

    /// <summary>
    /// Drains the durable outbox: sends every pending event, marking each delivered
    /// (which trims it from the outbox file). Runs on each heartbeat once connected.
    /// Events the server has already seen are deduped server-side via EventId, so
    /// replaying after a reinstall or crash never creates duplicates.
    /// </summary>
    private async Task FlushPendingEventsAsync()
    {
        if (_hub?.State != HubConnectionState.Connected) return;

        var pending = _store.GetPending();
        if (pending.Count == 0) return;

        var delivered = new List<Guid>();
        foreach (var payload in pending)
        {
            try
            {
                await _hub.SendAsync("DeviceChanged", payload);
                delivered.Add(payload.EventId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Flush interrupted - will resume on next heartbeat");
                break; // connection likely dropped; keep the rest in the outbox
            }
        }

        if (delivered.Count > 0)
        {
            _store.MarkDelivered(delivered);
            _log.LogInformation("Flushed {N} buffered event(s) from durable outbox", delivered.Count);
        }
    }

    private async Task<string?> ResolveTokenAsync(CancellationToken ct)
    {
        var env = Environment.GetEnvironmentVariable("PM_JwtToken");
        if (!string.IsNullOrEmpty(env)) { _log.LogInformation("Token: environment"); return env; }

        var cfg = _config["JwtToken"];
        if (!string.IsNullOrEmpty(cfg)) { _log.LogInformation("Token: config"); return cfg; }

        _log.LogInformation("Waiting for appsettings.local.json (5 min)...");
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                try
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, ct));
                    if (d?.TryGetValue("JwtToken", out var t) == true && !string.IsNullOrEmpty(t)) return t;
                }
                catch { }
            }
            await Task.Delay(3000, ct);
        }
        return null;
    }

    private static Guid ExtractInstitutionId(string jwt)
    {
        try
        {
            var raw = new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims
                          .FirstOrDefault(c => c.Type == "institution_id")?.Value;
            return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
        }
        catch { return Guid.Empty; }
    }

    private void BuildHub(string token)
    {
        var url = _config["SignalRUrl"] ?? Environment.GetEnvironmentVariable("PM_SignalRUrl") ?? "http://localhost:5000/monitor";
        _log.LogInformation("Hub URL: {Url}", url);
        _hub = new HubConnectionBuilder()
            .WithUrl(url, o =>
            {
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets |
                               Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) })
            .Build();
        _hub.Closed      += ex => { _log.LogWarning("Hub closed: {M}", ex?.Message); return Task.CompletedTask; };
        _hub.Reconnected += id => { _log.LogInformation("Hub reconnected: {Id}", id); return Task.CompletedTask; };

        _hub.On<AgentCommandPayload>("ExecuteCommand", async cmd =>
        {
            _log.LogWarning("Received command {Id}: {Type} (delay={Delay}s)", cmd.CommandId, cmd.Type, cmd.DelaySeconds);
            var result = await _commands.ExecuteAsync(cmd);
            try { await _hub!.SendAsync("CommandResult", result); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to send command result"); }
        });

        _hub.On<FilePushRequest>("PushFile", async req =>
        {
            _log.LogInformation("Receiving pushed file: {Name}", req.FileName);
            var result = await _files.SaveFileAsync(req);
            try { await _hub!.SendAsync("CommandResult", result); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to send push result"); }
        });

        _hub.On<FilePullRequest>("PullFile", async req =>
        {
            _log.LogInformation("Sending requested file: {Name}", req.FileName);
            var result = await _files.ReadFileAsync(req);
            try { await _hub!.SendAsync("FilePullResult", result); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to send pull result"); }
        });

        _hub.On<AppControlPolicy>("ApplyAppControlPolicy", policy =>
        {
            _log.LogInformation("Received Application Control policy (enabled={En})", policy?.Enabled);
            _appControl.Apply(policy);
        });

        _hub.On<FileDeleteRequest>("DeleteFile", async req =>
        {
            _log.LogInformation("Deleting shared file: {Name}", req.FileName);
            var result = _files.DeleteFile(req);
            try { await _hub!.SendAsync("FileDeleteResult", result); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to send delete result"); }
        });

        _hub.On<string>("ListSharedFolder", async commandId =>
        {
            var listing = _files.ListFiles(commandId);
            try { await _hub!.SendAsync("SharedFolderListing", listing); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to send folder listing"); }
        });

        // Remote desktop tunnel: server asks us to connect to our own local RDP/VNC port
        // and relay bytes both directions, since this machine likely has no inbound access.
        _hub.On<string, int>("StartTunnel", async (sessionId, targetPort) =>
        {
            _log.LogInformation("Starting remote desktop tunnel {Session} -> local port {Port}", sessionId, targetPort);
            await _tunnel.StartAsync(_hub!, sessionId, targetPort);
        });

        _hub.On<string>("StopTunnel", sessionId =>
        {
            _log.LogInformation("Stopping remote desktop tunnel {Session}", sessionId);
            _tunnel.Stop();
        });

        _hub.On<string, byte[]>("TunnelData", async (sessionId, data) =>
        {
            await _tunnel.OnServerDataAsync(sessionId, data);
        });
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _hub!.StartAsync(ct); _log.LogInformation("Hub connected ({Id})", _hub.ConnectionId); return; }
            catch (Exception ex) { _log.LogWarning(ex, "Hub connect failed — retry 10s"); await Task.Delay(10000, ct); }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _monitor.DeviceChanged -= OnDeviceChanged;
        _monitor.Stop();
        _appControl.Dispose();   // tears down the process watcher cleanly
        if (_hub?.State == HubConnectionState.Connected) await _hub.StopAsync(ct);
        _log.LogInformation("Agent stopped");
        await base.StopAsync(ct);
    }
}

internal static class NetHelpers
{
    public static string GetMac() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                                 n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            ?.GetPhysicalAddress().ToString() ?? "UNKNOWN";
}
