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
    private readonly ILogger<MonitorHub> _log;

    public MonitorHub(AgentService agents, AgentConnectionRegistry connections, ILogger<MonitorHub> log)
    { _agents = agents; _connections = connections; _log = log; }

    public async Task AgentHeartbeat(HeartbeatPayload p)
    {
        _log.LogDebug("Heartbeat: {Machine} inst={Inst} devices={N}", p.MachineName, p.InstitutionId, p.DeviceCount);
        var agentId = await _agents.UpdateHeartbeatAsync(p.MachineName, p.MacAddress, p.Platform, p.InstitutionId, p.Timestamp, p.DeviceCount, p.RustDeskId);
        if (agentId != Guid.Empty)
            _connections.Register(agentId, Context.ConnectionId);

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

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _connections.Unregister(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private static string InstGroup(Guid id) => $"inst:{id}";
}
