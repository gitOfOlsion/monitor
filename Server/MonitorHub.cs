using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server;

[AllowAnonymous] // ✅ Only approved agents with valid JWT can connect
public class MonitorHub : Hub
{
    public async Task AgentHeartbeat(string machineName, DateTime timestamp) =>
        await Clients.All.SendAsync("OnAgentStatus", machineName, "Online", timestamp);

    public async Task DeviceChanged(string machineName, string eventType, string deviceJson) =>
        await Clients.All.SendAsync("OnDeviceChange", machineName, eventType, deviceJson);
}