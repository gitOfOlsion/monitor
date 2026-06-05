using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PeripheralMonitor.Server.Services;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Hubs;

[AllowAnonymous]
public class MonitorHub : Hub
{
    private readonly AgentService _agents;
    private readonly ILogger<MonitorHub> _log;

    public MonitorHub(AgentService agents, ILogger<MonitorHub> log) { _agents = agents; _log = log; }

    public async Task AgentHeartbeat(HeartbeatPayload p)
    {
        _log.LogDebug("Heartbeat: {Machine} inst={Inst} devices={N}", p.MachineName, p.InstitutionId, p.DeviceCount);
        await _agents.UpdateHeartbeatAsync(p.MachineName, p.InstitutionId, p.Timestamp, p.DeviceCount);
        await Clients.Group(InstGroup(p.InstitutionId)).SendAsync("OnAgentHeartbeat", p);
        await Clients.Group("superadmin").SendAsync("OnAgentHeartbeat", p);
    }

    public async Task DeviceChanged(DeviceChangedPayload p)
    {
        _log.LogInformation("[{Event}] {Machine} inst={Inst} — {Name}", p.EventType, p.MachineName, p.InstitutionId, p.FriendlyName);
        await _agents.RecordDeviceEventAsync(p);
        await Clients.Group(InstGroup(p.InstitutionId)).SendAsync("OnDeviceChanged", p);
        await Clients.Group("superadmin").SendAsync("OnDeviceChanged", p);
    }

    public async Task JoinDashboard(string role, string? institutionId)
    {
        if (role == "SuperAdmin")
            await Groups.AddToGroupAsync(Context.ConnectionId, "superadmin");
        else if (Guid.TryParse(institutionId, out var id))
            await Groups.AddToGroupAsync(Context.ConnectionId, InstGroup(id));
    }

    private static string InstGroup(Guid id) => $"inst:{id}";
}
