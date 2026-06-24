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

    // Events detected while the SignalR connection is down (e.g. during the brief
    // reconnect window after a network blip - exactly when a "tunnel disconnected"
    // event is most likely to fire) are buffered here instead of silently dropped,
    // then flushed as soon as the connection is back up.
    private readonly System.Collections.Concurrent.ConcurrentQueue<DeviceChangedPayload> _pendingEvents = new();

    public AgentWorker(IConfiguration c, IDeviceMonitor m, DiscoveryService d, CommandExecutor cmd, FileShareService files, TunnelClient tunnel, ILogger<AgentWorker> l)
    { _config = c; _monitor = m; _discovery = d; _commands = cmd; _files = files; _tunnel = tunnel; _log = l; }

    /// <summary>Returns the currently logged-in interactive user, or null if none.</summary>
    private static string? GetLoggedInUser()
    {
        if (!OperatingSystem.IsWindows()) return GetLoggedInUserLinux();
        try
        {
            // qwinsta (Query Windows Station) lists all active RDP/console sessions
            // including the logged-in username. Works from any service account without
            // any WMI permissions — confirmed working from PeripheralMonitorSvc context.
            // Output format: "SESSIONNAME  USERNAME  ID  STATE  TYPE  DEVICE"
            // We look for lines with STATE=Active and extract the USERNAME column.
            var psi = new System.Diagnostics.ProcessStartInfo("qwinsta")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            // Parse each line looking for Active sessions with a username
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("Active", StringComparison.OrdinalIgnoreCase)) continue;

                // Split on whitespace — columns: SESSIONNAME USERNAME ID STATE ...
                var parts = line.Trim().TrimStart('>') // remove the '>' marker for current session
                                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                // Need at least 4 columns and the username must not be numeric (avoid ID column)
                if (parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[1])
                    && !int.TryParse(parts[1], out _))
                    return parts[1]; // username is always the second token
            }
        }
        catch { /* non-fatal */ }
        return null;
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
                    await _hub.SendAsync("AgentHeartbeat",
                        new HeartbeatPayload(_machineName, _macAddress, _institutionId, _platform.ToString(),
                            DateTime.UtcNow, snap.Count, loggedInUser, cpu, cores, ramMb, disk), ct);
                    _log.LogDebug("Heartbeat — {N} devices, User={User}", snap.Count, loggedInUser ?? "none");
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

        if (_hub?.State != HubConnectionState.Connected)
        {
            // Buffer instead of dropping - this is exactly when a flaky network
            // adapter event is likely to coincide with our own connection blipping.
            _pendingEvents.Enqueue(payload);
            _log.LogWarning("[{Event}] {Cat} — {Name} (hub disconnected, buffered for later send)", eventType, dev.Category, dev.FriendlyName);
            return;
        }

        try
        {
            await _hub.SendAsync("DeviceChanged", payload);
            _log.LogInformation("[{Event}] {Cat} — {Name}", eventType, dev.Category, dev.FriendlyName);
        }
        catch (Exception ex)
        {
            _pendingEvents.Enqueue(payload);
            _log.LogWarning(ex, "DeviceChanged send error - buffered for retry");
        }
    }

    /// <summary>Flushes any device-change events that were buffered while disconnected.</summary>
    private async Task FlushPendingEventsAsync()
    {
        if (_hub?.State != HubConnectionState.Connected) return;
        while (_pendingEvents.TryDequeue(out var payload))
        {
            try
            {
                await _hub.SendAsync("DeviceChanged", payload);
                _log.LogInformation("[Flushed] {Event} {Cat} — {Name}", payload.EventType, payload.Category, payload.FriendlyName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to flush buffered event - re-queueing");
                _pendingEvents.Enqueue(payload);
                break; // stop trying, connection likely dropped again
            }
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
