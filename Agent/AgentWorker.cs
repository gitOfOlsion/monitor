using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using PeripheralMonitor.Agent.Services;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Agent;

public class AgentWorker : BackgroundService
{
    private HubConnection?              _hub;
    private DeviceMonitorEngine?        _engine;
    private readonly DiscoveryService   _discovery;
    private readonly IConfiguration     _config;
    private readonly ILogger<AgentWorker> _logger;
    private readonly string             _machineName = Environment.MachineName;
    private readonly string             _macAddress  = GetMacAddress();

    public AgentWorker(
        IConfiguration        config,
        DiscoveryService      discovery,
        ILogger<AgentWorker>  logger)
    {
        _config    = config;
        _discovery = discovery;
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent starting on {Machine}", _machineName);
        _discovery.StartBroadcasting();

        var approved = await WaitForApprovalAsync(stoppingToken);
        if (!approved)
        {
            _logger.LogWarning("Approval timeout — continuing to broadcast");
            while (!stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            return;
        }

        InitializeHub();

        try
        {
            await _hub!.StartAsync(stoppingToken);
            _logger.LogInformation("Connected to SignalR hub");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SignalR connection failed");
        }

        // Start the three-layer device monitor engine
        // Pass null for serviceBase — Layer 1 gracefully skips,
        // Layers 2+3 handle everything (SetupAPI + WMI)
        _engine = new DeviceMonitorEngine(
            _logger as ILogger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        _engine.DeviceChanged += OnDeviceChanged;
        await _engine.StartAsync();

        // Heartbeat loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_hub?.State == HubConnectionState.Connected)
                {
                    await _hub.SendAsync("AgentHeartbeat", _machineName, DateTime.UtcNow, stoppingToken);
                    _logger.LogDebug("Heartbeat sent");
                }
                else if (_hub != null)
                {
                    try { await _hub.StartAsync(stoppingToken); }
                    catch { /* retry next cycle */ }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Heartbeat error");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task OnDeviceChanged(string eventType, DeviceInfo device)
    {
        try
        {
            if (_hub?.State != HubConnectionState.Connected)
            {
                _logger.LogWarning("Hub not connected — skipping device event: {Name}", device.FriendlyName);
                return;
            }

            var payload = new
            {
                MachineName  = _machineName,
                EventType    = eventType,
                DeviceId     = device.DeviceId,
                FriendlyName = device.FriendlyName,
                Category     = device.Category,
                Status       = device.Status,
                Timestamp    = DateTime.UtcNow
            };

            await _hub.SendAsync("DeviceChanged",
                _machineName,
                eventType,
                JsonSerializer.Serialize(payload));

            _logger.LogInformation("[{Event}] {Category} — {Name}", eventType, device.Category, device.FriendlyName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnDeviceChanged send error");
        }
    }

    private async Task<bool> WaitForApprovalAsync(CancellationToken ct)
    {
        var exeDir          = AppContext.BaseDirectory;
        var localConfigPath = System.IO.Path.Combine(exeDir, "appsettings.local.json");

        _logger.LogInformation("Looking for approval token at: {Path}", localConfigPath);

        if (System.IO.File.Exists(localConfigPath))
        {
            try
            {
                var json   = await System.IO.File.ReadAllTextAsync(localConfigPath, ct);
                var config = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (config?.ContainsKey("JwtToken") == true && !string.IsNullOrEmpty(config["JwtToken"]))
                {
                    _logger.LogInformation("Using saved approval token");
                    return true;
                }
            }
            catch { }
        }

        var timeout = TimeSpan.FromSeconds(60);
        var start   = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout && !ct.IsCancellationRequested)
        {
            if (System.IO.File.Exists(localConfigPath))
            {
                try
                {
                    var json   = await System.IO.File.ReadAllTextAsync(localConfigPath, ct);
                    var config = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (config?.ContainsKey("JwtToken") == true && !string.IsNullOrEmpty(config["JwtToken"]))
                    {
                        _logger.LogInformation("Approval token received");
                        return true;
                    }
                }
                catch { }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return false;
    }

    private void InitializeHub()
    {
        var exeDir          = AppContext.BaseDirectory;
        var localConfigPath = System.IO.Path.Combine(exeDir, "appsettings.local.json");
        var serverUrl       = _config["SignalRUrl"] ?? "http://localhost:5000/monitor";
        var jwtToken        = _config["JwtToken"];

        // Read token directly from file if config hasn't reloaded
        if (string.IsNullOrEmpty(jwtToken) && System.IO.File.Exists(localConfigPath))
        {
            try
            {
                var json   = System.IO.File.ReadAllText(localConfigPath);
                var config = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                config?.TryGetValue("JwtToken", out jwtToken);
            }
            catch { }
        }

        _logger.LogInformation("SignalRUrl: {Url}", serverUrl);
        _logger.LogInformation("JwtToken present: {Present}", !string.IsNullOrEmpty(jwtToken));

        _hub = new HubConnectionBuilder()
            .WithUrl(serverUrl, options =>
            {
                options.Transports =
                    Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets |
                    Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;

                if (!string.IsNullOrEmpty(jwtToken))
                    options.AccessTokenProvider = () => Task.FromResult<string?>(jwtToken);
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        _hub.Closed      += ex => { _logger.LogWarning("Hub closed: {Msg}", ex?.Message); return Task.CompletedTask; };
        _hub.Reconnected += id => { _logger.LogInformation("Hub reconnected: {Id}", id);  return Task.CompletedTask; };
    }

    private static string GetMacAddress()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n =>
                n.OperationalStatus    == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        return nic?.GetPhysicalAddress().ToString() ?? "UNKNOWN";
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        _engine?.Stop();
        _engine?.Dispose();

        if (_hub?.State == HubConnectionState.Connected)
            await _hub.StopAsync(stoppingToken);

        _logger.LogInformation("Agent stopped");
        await base.StopAsync(stoppingToken);
    }
}
