using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PeripheralMonitor.Server.Services;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Hubs;

[AllowAnonymous]
public class MonitorHub : Hub
{
    private readonly AgentService _agents;
    private readonly AgentConnectionRegistry _connections;
    private readonly TunnelManager _tunnels;
    private readonly ILogger<MonitorHub> _log;

    public MonitorHub(AgentService agents, AgentConnectionRegistry connections, TunnelManager tunnels, ILogger<MonitorHub> log)
    { _agents = agents; _connections = connections; _tunnels = tunnels; _log = log; }

    public async Task AgentHeartbeat(HeartbeatPayload p)
    {
        _log.LogDebug("Heartbeat: {Machine} inst={Inst} devices={N}", p.MachineName, p.InstitutionId, p.DeviceCount);
        var (agentId, userEvent) = await _agents.UpdateHeartbeatAsync(p.MachineName, p.MacAddress, p.Platform, p.InstitutionId, p.Timestamp, p.DeviceCount, p.LoggedInUser);
        if (agentId != Guid.Empty)
            _connections.Register(agentId, Context.ConnectionId);

        await Clients.Group(InstGroup(p.InstitutionId)).SendAsync("OnAgentHeartbeat", p);
        await Clients.Group("superadmin").SendAsync("OnAgentHeartbeat", p);

        // Broadcast login/logout events to dashboards in real time
        if (userEvent != null)
        {
            await Clients.Group(InstGroup(p.InstitutionId)).SendAsync("OnDeviceChanged", userEvent);
            await Clients.Group("superadmin").SendAsync("OnDeviceChanged", userEvent);
        }
    }

    public async Task DeviceChanged(DeviceChangedPayload p)
    {
        _log.LogInformation("[{Event}] {Machine} inst={Inst} — {Name}", p.EventType, p.MachineName, p.InstitutionId, p.FriendlyName);
        await _agents.RecordDeviceEventAsync(p);
        await Clients.Group(InstGroup(p.InstitutionId)).SendAsync("OnDeviceChanged", p);
        await Clients.Group("superadmin").SendAsync("OnDeviceChanged", p);
    }

    /// <summary>Agent reports the outcome of a previously-issued command (restart/shutdown/file push).</summary>
    public Task CommandResult(AgentCommandResult result)
    {
        _log.LogInformation("Command result {Id}: success={Success} {Msg}", result.CommandId, result.Success, result.Message);
        _connections.CompleteResult(result.CommandId, result);
        return Task.CompletedTask;
    }

    /// <summary>Agent reports the content of a requested file (file pull).</summary>
    public Task FilePullResult(FilePullResult result)
    {
        _log.LogInformation("File pull result {Id}: success={Success} file={File}", result.CommandId, result.Success, result.FileName);
        _connections.CompleteResult(result.CommandId, result);
        return Task.CompletedTask;
    }

    /// <summary>Agent reports the contents of its shared folder.</summary>
    public Task SharedFolderListing(SharedFolderListing listing)
    {
        _log.LogInformation("Folder listing {Id}: success={Success} files={N}", listing.CommandId, listing.Success, listing.Files?.Count ?? 0);
        _connections.CompleteResult(listing.CommandId, listing);
        return Task.CompletedTask;
    }

    public async Task JoinDashboard(string role, string? institutionId)
    {
        if (role == "SuperAdmin")
            await Groups.AddToGroupAsync(Context.ConnectionId, "superadmin");
        else if (Guid.TryParse(institutionId, out var id))
            await Groups.AddToGroupAsync(Context.ConnectionId, InstGroup(id));
    }

    /// <summary>Agent signals it has connected to its local RDP port and is ready to relay.</summary>
    public Task TunnelReady(string sessionId)
    {
        var agentId = _connections.GetAgentIdForConnection(Context.ConnectionId);
        if (agentId.HasValue)
        {
            _log.LogDebug("TunnelReady from agent {Agent} session {Session}", agentId.Value, sessionId);
            _tunnels.OnAgentReady(agentId.Value);
        }
        return Task.CompletedTask;
    }

    /// <summary>Agent forwards raw bytes from its local RDP/VNC connection back to Guacamole.</summary>
    public async Task TunnelDataFromAgent(string sessionId, byte[] data)
    {
        var agentId = _connections.GetAgentIdForConnection(Context.ConnectionId);
        if (agentId.HasValue)
            await _tunnels.OnAgentDataAsync(agentId.Value, data);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var agentId = _connections.GetAgentIdForConnection(Context.ConnectionId);
        _connections.Unregister(Context.ConnectionId);

        if (agentId.HasValue)
        {
            // Don't immediately tear down any active tunnel — the agent's SignalR connection
            // can briefly drop during an RDP session startup (Windows reprioritizing network,
            // VPN blip, etc.) and will reconnect within seconds. Give it a 20-second grace
            // period before considering the tunnel dead. The tunnel's own idle-timeout and
            // the guacd connection closure will clean up naturally if the agent doesn't return.
            var capturedId = agentId.Value;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                // Only stop the tunnel if the agent is STILL offline after the grace period
                if (_connections.GetConnectionId(capturedId) == null)
                {
                    _log.LogInformation("Agent {Agent} still offline after 20s grace — stopping tunnel", capturedId);
                    await _tunnels.StopTunnelAsync(capturedId);
                }
            });
        }

        return base.OnDisconnectedAsync(exception);
    }

    private static string InstGroup(Guid id) => $"inst:{id}";
}
