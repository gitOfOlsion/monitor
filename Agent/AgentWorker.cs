using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using PeripheralMonitor.Agent.Services;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent;

public class AgentWorker : BackgroundService
{
    private HubConnection?               _hub;
    private readonly IDeviceMonitor      _monitor;
    private readonly DiscoveryService    _discovery;
    private readonly IConfiguration      _config;
    private readonly ILogger<AgentWorker> _logger;

    private readonly string        _machineName = Environment.MachineName;
    private readonly string        _macAddress  = NetworkHelpers.GetMacAddress();
    private readonly string        _ipAddress   = NetworkHelpers.GetLocalIp();
    private readonly AgentPlatform _platform    = OperatingSystem.IsWindows()
        ? AgentPlatform.Windows : AgentPlatform.Linux;

    public AgentWorker(
        IConfiguration        config,
        IDeviceMonitor        monitor,
        DiscoveryService      discovery,
        ILogger<AgentWorker>  logger)
    {
        _config    = config;
        _monitor   = monitor;
        _discovery = discovery;
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Agent starting — {Machine} | {Platform} | {Mac}",
            _machineName, _platform, _macAddress);

        // Resolve JWT token (env var → appsettings.local.json → wait)
        var token = await ResolveTokenAsync(ct);
        if (token == null)
        {
            _logger.LogWarning("No JWT token available. Set PM_JwtToken or approve via dashboard.");
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return;
        }

        // Broadcast discovery with JWT so server can resolve institution
        _discovery.StartBroadcasting(_machineName, _macAddress, _platform, token);

        // Connect to SignalR hub
        BuildHub(token);
        await ConnectHubAsync(ct);

        // Self-register on hub (server resolves institution from JWT claims)
        await _hub!.SendAsync("RegisterAgent",
            _machineName, _macAddress, _ipAddress, _platform.ToString(), ct);

        // Start device monitoring
        _monitor.DeviceChanged += OnDeviceChanged;
        await _monitor.StartAsync(ct);

        // Heartbeat loop
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
                    var snapshot = await _monitor.GetSnapshotAsync();
                    await _hub.SendAsync("AgentHeartbeat",
                        new HeartbeatPayload(_machineName, _macAddress, DateTime.UtcNow, snapshot.Count), ct);
                    _logger.LogDebug("Heartbeat — {Count} devices", snapshot.Count);
                }
                else if (_hub?.State is HubConnectionState.Disconnected)
                {
                    try { await _hub.StartAsync(ct); }
                    catch { }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogWarning(ex, "Heartbeat error"); }

            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private async Task OnDeviceChanged(string eventType, DeviceInfo device)
    {
        if (_hub?.State != HubConnectionState.Connected) return;
        try
        {
            await _hub.SendAsync("DeviceChanged", new DeviceChangedPayload(
                _machineName, eventType, device.DeviceId,
                device.FriendlyName, device.Category, device.Status, DateTime.UtcNow));
            _logger.LogInformation("[{Event}] {Cat} — {Name}", eventType, device.Category, device.FriendlyName);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "DeviceChanged send error"); }
    }

    private async Task<string?> ResolveTokenAsync(CancellationToken ct)
    {
        // Priority 1: environment variable
        var env = Environment.GetEnvironmentVariable("PM_JwtToken");
        if (!string.IsNullOrEmpty(env)) { _logger.LogInformation("JWT from env var"); return env; }

        // Priority 2: config file
        var cfg = _config["JwtToken"];
        if (!string.IsNullOrEmpty(cfg)) { _logger.LogInformation("JWT from config"); return cfg; }

        // Priority 3: wait for appsettings.local.json (interactive approval)
        _logger.LogInformation("Waiting for JWT token (up to 5 min)...");
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
            if (File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path, ct);
                    var map  = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (map?.TryGetValue("JwtToken", out var t) == true && !string.IsNullOrEmpty(t))
                    {
                        _logger.LogInformation("JWT received from file");
                        return t;
                    }
                }
                catch { }
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return null;
    }

    private void BuildHub(string token)
    {
        var url = _config["SignalRUrl"]
               ?? Environment.GetEnvironmentVariable("PM_SignalRUrl")
               ?? "http://localhost:5000/monitor";

        _logger.LogInformation("Hub URL: {Url}", url);

        _hub = new HubConnectionBuilder()
            .WithUrl(url, opts =>
            {
                opts.Transports =
                    Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets |
                    Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                opts.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .WithAutomaticReconnect([
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            ])
            .Build();

        _hub.Closed      += ex => { _logger.LogWarning("Hub closed: {Msg}", ex?.Message); return Task.CompletedTask; };
        _hub.Reconnected += id => { _logger.LogInformation("Hub reconnected: {Id}", id);  return Task.CompletedTask; };
        _hub.Reconnecting+= ex => { _logger.LogWarning("Hub reconnecting: {Msg}", ex?.Message); return Task.CompletedTask; };
    }

    private async Task ConnectHubAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _hub!.StartAsync(ct);
                _logger.LogInformation("Hub connected (ID: {Id})", _hub.ConnectionId);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hub connect failed — retry in 10s");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _monitor.DeviceChanged -= OnDeviceChanged;
        _monitor.Stop();
        if (_hub?.State == HubConnectionState.Connected)
            await _hub.StopAsync(ct);
        _logger.LogInformation("Agent stopped");
        await base.StopAsync(ct);
    }
}

internal static class NetworkHelpers
{
    public static string GetMacAddress()
    {
        var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n =>
                n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);
        return nic?.GetPhysicalAddress().ToString() ?? "UNKNOWN";
    }

    public static string GetLocalIp()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            return host.AddressList
                .FirstOrDefault(ip =>
                    ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !System.Net.IPAddress.IsLoopback(ip))?.ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }
}
