using Microsoft.AspNetCore.SignalR.Client;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Web.Services;

public class RealtimeService : IAsyncDisposable
{
    private HubConnection?    _hub;
    private readonly IConfiguration _config;
    private readonly ILogger<RealtimeService> _logger;

    public event Action<HeartbeatPayload>?     OnHeartbeat;
    public event Action<DeviceChangedPayload>? OnDeviceChanged;
    public HubConnectionState State => _hub?.State ?? HubConnectionState.Disconnected;

    public RealtimeService(IConfiguration config, ILogger<RealtimeService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task EnsureConnectedAsync(string institutionId, bool isSuperAdmin)
    {
        if (_hub?.State == HubConnectionState.Connected) return;

        var url = (_config["ServerApiUrl"] ?? "http://server:5000") + "/monitor";

        // Web-to-hub connection uses cookie auth (no token needed for dashboard)
        _hub = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect([
                TimeSpan.Zero,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(10)
            ])
            .Build();

        _hub.On<HeartbeatPayload>("OnAgentHeartbeat",   p => OnHeartbeat?.Invoke(p));
        _hub.On<DeviceChangedPayload>("OnDeviceChanged", p => OnDeviceChanged?.Invoke(p));

        _hub.Reconnected += async _ =>
        {
            _logger.LogInformation("Realtime hub reconnected");
            await _hub.SendAsync("JoinDashboard", institutionId, isSuperAdmin);
        };

        try
        {
            await _hub.StartAsync();
            await _hub.SendAsync("JoinDashboard", institutionId, isSuperAdmin);
            _logger.LogInformation("Realtime hub connected");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Realtime hub failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub != null) await _hub.DisposeAsync();
    }
}
