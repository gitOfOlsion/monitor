using Microsoft.AspNetCore.SignalR.Client;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Web.Services;

public class RealtimeService : IAsyncDisposable
{
    private HubConnection? _hub;
    private readonly IConfiguration _config;
    private readonly ILogger<RealtimeService> _log;

    public event Action<HeartbeatPayload>?     OnHeartbeat;
    public event Action<DeviceChangedPayload>? OnDeviceChanged;
    public HubConnectionState State => _hub?.State ?? HubConnectionState.Disconnected;

    public RealtimeService(IConfiguration config, ILogger<RealtimeService> log) { _config = config; _log = log; }

    public async Task EnsureConnectedAsync(string role = "Viewer", string? institutionId = null)
    {
        if (_hub?.State == HubConnectionState.Connected) return;

        var url = (_config["ServerApiUrl"] ?? "http://server:5000") + "/monitor";
        _hub = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) })
            .Build();

        _hub.On<HeartbeatPayload>("OnAgentHeartbeat",  p => OnHeartbeat?.Invoke(p));
        _hub.On<DeviceChangedPayload>("OnDeviceChanged", p => OnDeviceChanged?.Invoke(p));
        _hub.Reconnected += async _ => { await _hub.SendAsync("JoinDashboard", role, institutionId); };

        try
        {
            await _hub.StartAsync();
            await _hub.SendAsync("JoinDashboard", role, institutionId);
            _log.LogInformation("Realtime hub connected");
        }
        catch (Exception ex) { _log.LogWarning(ex, "Realtime hub connection failed"); }
    }

    public async ValueTask DisposeAsync() { if (_hub != null) await _hub.DisposeAsync(); }
}
