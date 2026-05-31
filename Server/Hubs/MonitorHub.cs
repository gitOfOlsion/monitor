using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PeripheralMonitor.Server.Services;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Hubs;

[Authorize(Policy = "AgentOrDashboard")]
public class MonitorHub : Hub
{
    private readonly AgentService _agents;
    private readonly ILogger<MonitorHub> _logger;

    public MonitorHub(AgentService agents, ILogger<MonitorHub> logger)
    {
        _agents = agents;
        _logger = logger;
    }

    // ── Called by agents ──────────────────────────────────────────────────
    public async Task AgentHeartbeat(HeartbeatPayload payload)
    {
        var institutionId = GetInstitutionId();
        _logger.LogDebug("Heartbeat from {Machine} (inst {Inst})", payload.MachineName, institutionId);

        await _agents.UpdateHeartbeatAsync(payload.MachineName, payload.Timestamp, payload.DeviceCount);

        // Broadcast to institution-specific group AND superadmin group
        await Clients.Group($"inst:{institutionId}").SendAsync("OnAgentHeartbeat", payload);
        await Clients.Group("superadmin").SendAsync("OnAgentHeartbeat", payload);
    }

    public async Task DeviceChanged(DeviceChangedPayload payload)
    {
        var institutionId = GetInstitutionId();
        _logger.LogInformation("[{Event}] {Machine} — {Name} ({Cat})",
            payload.EventType, payload.MachineName, payload.FriendlyName, payload.Category);

        await _agents.RecordDeviceEventAsync(payload);

        await Clients.Group($"inst:{institutionId}").SendAsync("OnDeviceChanged", payload);
        await Clients.Group("superadmin").SendAsync("OnDeviceChanged", payload);
    }

    // ── Called by dashboard web clients ──────────────────────────────────
    /// <summary>Dashboard joins its institution group (or superadmin group).</summary>
    public async Task JoinDashboard(string institutionId, bool isSuperAdmin)
    {
        if (isSuperAdmin)
            await Groups.AddToGroupAsync(Context.ConnectionId, "superadmin");
        else
            await Groups.AddToGroupAsync(Context.ConnectionId, $"inst:{institutionId}");

        _logger.LogDebug("Dashboard joined group (inst={Inst}, super={Super})",
            institutionId, isSuperAdmin);
    }

    // ── Self-registration: agent connects with template JWT ───────────────
    public async Task RegisterAgent(string machineName, string macAddress,
        string ipAddress, string platform)
    {
        var institutionId = GetInstitutionId();
        var templateId    = GetTemplateId();

        if (string.IsNullOrEmpty(institutionId)) return;

        await _agents.RegisterFromHubAsync(
            machineName, macAddress, ipAddress, platform,
            Guid.Parse(institutionId),
            templateId != null ? Guid.Parse(templateId) : null);

        _logger.LogInformation("Agent self-registered: {Machine} (inst:{Inst})", machineName, institutionId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Client disconnected: {Id}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private string? GetInstitutionId()
        => Context.User?.FindFirst("institution_id")?.Value;

    private string? GetTemplateId()
        => Context.User?.FindFirst("template_id")?.Value;
}
