using Microsoft.EntityFrameworkCore;
using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Shared.Models;
using PeripheralMonitor.Shared.Security;

namespace PeripheralMonitor.Server.Services;

public class AgentService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration                  _config;
    private readonly ILogger<AgentService>           _logger;

    public AgentService(
        IDbContextFactory<AppDbContext> dbFactory,
        IConfiguration                  config,
        ILogger<AgentService>           logger)
    {
        _dbFactory = dbFactory;
        _config    = config;
        _logger    = logger;
    }

    // ── Query ─────────────────────────────────────────────────────────────
    public async Task<List<Agent>> GetAllAsync(Guid? institutionId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var q = db.Agents.Include(a => a.Template).AsQueryable();
        if (institutionId.HasValue) q = q.Where(a => a.InstitutionId == institutionId.Value);
        return await q.OrderByDescending(a => a.LastHeartbeat).ToListAsync();
    }

    public async Task<Agent?> GetByMacAsync(string macAddress)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Agents.FirstOrDefaultAsync(a => a.MacAddress == macAddress);
    }

    // ── Discovery registration ────────────────────────────────────────────
    // Called when an agent broadcasts UDP — we don't know institution yet at this point.
    // Institution is resolved when the agent connects to the hub and presents its JWT.
    public async Task<Agent> UpsertFromDiscoveryAsync(
        DiscoveryBroadcast broadcast, Guid institutionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var agent = await db.Agents
            .FirstOrDefaultAsync(a => a.MacAddress == broadcast.MacAddress);

        if (agent == null)
        {
            agent = new Agent
            {
                MachineName   = broadcast.MachineName,
                IPAddress     = broadcast.IPAddress,
                MacAddress    = broadcast.MacAddress,
                Platform      = broadcast.Platform.ToString(),
                InstitutionId = institutionId,
                FirstSeen     = DateTime.UtcNow,
                Status        = "Pending"
            };
            db.Agents.Add(agent);
            _logger.LogInformation("New agent discovered: {Machine} ({Mac}) for institution {Inst}",
                broadcast.MachineName, broadcast.MacAddress, institutionId);
        }
        else
        {
            agent.IPAddress = broadcast.IPAddress;
            agent.Platform  = broadcast.Platform.ToString();
        }

        await db.SaveChangesAsync();
        return agent;
    }

    // Called when agent connects to hub with JWT containing institution_id
    public async Task<Agent?> RegisterFromHubAsync(
        string machineName, string macAddress, string ipAddress,
        string platform, Guid institutionId, Guid? templateId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.MacAddress == macAddress);

        if (agent == null)
        {
            agent = new Agent
            {
                MachineName   = machineName,
                IPAddress     = ipAddress,
                MacAddress    = macAddress,
                Platform      = platform,
                InstitutionId = institutionId,
                TemplateId    = templateId,
                FirstSeen     = DateTime.UtcNow,
                Status        = "Pending"
            };
            db.Agents.Add(agent);
            _logger.LogInformation("Agent self-registered via hub: {Machine}", machineName);
        }
        else
        {
            agent.IPAddress     = ipAddress;
            agent.Platform      = platform;
            agent.InstitutionId = institutionId;
            if (templateId.HasValue) agent.TemplateId = templateId;
        }

        await db.SaveChangesAsync();
        return agent;
    }

    // ── Approve / Reject / Delete ─────────────────────────────────────────
    public async Task<(bool Success, string? Token)> ApproveAsync(
        Guid agentId, string approvedBy, Guid requestingInstitutionId, bool isSuperAdmin)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var agent = await db.Agents.FindAsync(agentId);
        if (agent == null) return (false, null);
        if (!isSuperAdmin && agent.InstitutionId != requestingInstitutionId) return (false, null);

        var secret = _config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret missing");
        var token  = JwtHelper.GenerateAgentToken(
            agent.MachineName, agent.MacAddress,
            agent.InstitutionId.ToString(), secret);

        agent.Status     = "Approved";
        agent.ApprovedBy = approvedBy;
        agent.ApprovedAt = DateTime.UtcNow;
        agent.JwtToken   = token;

        await db.SaveChangesAsync();
        _logger.LogInformation("Agent approved: {Machine} by {By}", agent.MachineName, approvedBy);
        return (true, token);
    }

    public async Task<bool> RejectAsync(Guid agentId, Guid institutionId, bool isSuperAdmin)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var agent = await db.Agents.FindAsync(agentId);
        if (agent == null) return false;
        if (!isSuperAdmin && agent.InstitutionId != institutionId) return false;
        agent.Status = "Rejected";
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid agentId, Guid institutionId, bool isSuperAdmin)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var agent = await db.Agents.FindAsync(agentId);
        if (agent == null || agent.Status == "Approved") return false;
        if (!isSuperAdmin && agent.InstitutionId != institutionId) return false;
        db.Agents.Remove(agent);
        await db.SaveChangesAsync();
        return true;
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────
    public async Task UpdateHeartbeatAsync(string machineName, DateTime ts, int deviceCount)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.MachineName == machineName);
        if (agent == null) return;
        agent.LastHeartbeat = ts;
        agent.DeviceCount   = deviceCount;
        if (agent.Status == "Offline") agent.Status = "Approved";
        await db.SaveChangesAsync();
    }

    // ── Device events ─────────────────────────────────────────────────────
    public async Task RecordDeviceEventAsync(DeviceChangedPayload payload)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.MachineName == payload.MachineName);
        if (agent == null) return;

        // Resolve category from DB
        var category = await db.DeviceCategories
            .FirstOrDefaultAsync(c => c.Name == payload.Category);

        db.DeviceEvents.Add(new DeviceEvent
        {
            AgentId     = agent.Id,
            CategoryId  = category?.Id,
            MachineName = payload.MachineName,
            DeviceId    = payload.DeviceId,
            FriendlyName= payload.FriendlyName,
            EventType   = payload.EventType,
            Status      = payload.Status,
            Timestamp   = payload.Timestamp
        });
        await db.SaveChangesAsync();
    }

    public async Task<List<DeviceEvent>> GetRecentEventsAsync(
        int count = 200, Guid? institutionId = null, Guid? agentId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var q = db.DeviceEvents
            .Include(e => e.Category)
            .Include(e => e.Agent)
            .AsQueryable();

        if (institutionId.HasValue)
            q = q.Where(e => e.Agent.InstitutionId == institutionId.Value);
        if (agentId.HasValue)
            q = q.Where(e => e.AgentId == agentId.Value);

        return await q.OrderByDescending(e => e.Timestamp).Take(count).ToListAsync();
    }

    public async Task MarkStaleAgentsOfflineAsync(TimeSpan staleness)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var cutoff = DateTime.UtcNow - staleness;
        var stale  = await db.Agents
            .Where(a => a.Status == "Approved" &&
                        (a.LastHeartbeat == null || a.LastHeartbeat < cutoff))
            .ToListAsync();
        foreach (var a in stale) a.Status = "Offline";
        if (stale.Count > 0) await db.SaveChangesAsync();
    }
}
