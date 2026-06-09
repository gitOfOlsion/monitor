using Microsoft.EntityFrameworkCore;
using PeripheralMonitor.Server.Data;
using PeripheralMonitor.Shared.Models;
using PeripheralMonitor.Shared.Security;

namespace PeripheralMonitor.Server.Services;

public class AgentService
{
    private readonly IDbContextFactory<AppDbContext> _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AgentService> _log;

    public AgentService(IDbContextFactory<AppDbContext> db, IConfiguration config, ILogger<AgentService> log)
    { _db = db; _config = config; _log = log; }

    private string JwtSecret => _config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured");

    // ── Agents ────────────────────────────────────────────────────────────
    public async Task<List<AgentEntity>> GetAgentsAsync(Guid? institutionId = null)
    {
        await using var db = await _db.CreateDbContextAsync();
        var q = db.Agents.Include(a => a.Institution).Include(a => a.Template).AsQueryable();
        if (institutionId.HasValue) q = q.Where(a => a.InstitutionId == institutionId.Value);
        return await q.OrderByDescending(a => a.LastHeartbeat).ToListAsync();
    }

    public async Task<AgentEntity> UpsertFromDiscoveryAsync(DiscoveryBroadcast b)
    {
        await using var db = await _db.CreateDbContextAsync();
        if (!await db.Institutions.AnyAsync(i => i.Id == b.InstitutionId))
            throw new InvalidOperationException($"Unknown institution {b.InstitutionId}");

        var agent = await db.Agents.FirstOrDefaultAsync(a =>
            a.MacAddress == b.MacAddress && a.InstitutionId == b.InstitutionId);

        if (agent == null)
        {
            agent = new AgentEntity
            {
                InstitutionId = b.InstitutionId, MachineName = b.MachineName,
                IPAddress = b.IPAddress, MacAddress = b.MacAddress,
                Platform = b.Platform.ToString(), Status = "Pending"
            };
            db.Agents.Add(agent);
            _log.LogInformation("New agent: {Machine} ({Mac}) inst={Inst}", b.MachineName, b.MacAddress, b.InstitutionId);
        }
        else { agent.IPAddress = b.IPAddress; agent.Platform = b.Platform.ToString(); }

        await db.SaveChangesAsync();
        return agent;
    }

    public async Task<(bool Ok, string? Token)> ApproveAsync(Guid agentId, string by, Guid? callerInst)
    {
        await using var db = await _db.CreateDbContextAsync();
        var agent = await db.Agents.FindAsync(agentId);
        if (agent == null) return (false, null);
        if (callerInst.HasValue && agent.InstitutionId != callerInst.Value) return (false, null);

        var token = JwtHelper.GenerateAgentToken(agent.MachineName, agent.MacAddress, agent.InstitutionId, JwtSecret);
        agent.Status = "Approved"; agent.ApprovedBy = by; agent.ApprovedAt = DateTime.UtcNow; agent.JwtToken = token;
        await db.SaveChangesAsync();
        return (true, token);
    }

    public async Task<bool> RejectAsync(Guid agentId, Guid? callerInst)
    {
        await using var db = await _db.CreateDbContextAsync();
        var a = await db.Agents.FindAsync(agentId);
        if (a == null || (callerInst.HasValue && a.InstitutionId != callerInst.Value)) return false;
        a.Status = "Rejected"; await db.SaveChangesAsync(); return true;
    }

    public async Task<bool> DeleteAsync(Guid agentId, Guid? callerInst)
    {
        await using var db = await _db.CreateDbContextAsync();
        var a = await db.Agents.FindAsync(agentId);
        if (a == null || a.Status == "Approved") return false;
        if (callerInst.HasValue && a.InstitutionId != callerInst.Value) return false;
        db.Agents.Remove(a); await db.SaveChangesAsync(); return true;
    }

    public async Task UpdateHeartbeatAsync(string machineName, string macAddress, Guid institutionId, DateTime ts, int count)
    {
        await using var db = await _db.CreateDbContextAsync();
        var a = await db.Agents.FirstOrDefaultAsync(x => x.MachineName == machineName && x.InstitutionId == institutionId);

        // Auto-register: agent has a valid institution JWT but no DB record yet
        // This happens when agents connect over the internet (no UDP discovery)
        if (a == null)
        {
            if (!await db.Institutions.AnyAsync(i => i.Id == institutionId))
                return; // invalid institution — ignore

            a = new AgentEntity
            {
                InstitutionId = institutionId,
                MachineName   = machineName,
                IPAddress     = "remote",
                MacAddress    = macAddress ?? "unknown",
                Platform      = "Unknown",
                FirstSeen     = DateTime.UtcNow,
                Status        = "Approved", // auto-approve — JWT is proof of authorization
                ApprovedBy    = "auto-provision",
                ApprovedAt    = DateTime.UtcNow
            };
            db.Agents.Add(a);
            _logger.LogInformation("Auto-registered agent: {Machine} ({Mac}) for institution {Inst}",
                machineName, macAddress, institutionId);
        }

        a.LastHeartbeat = ts;
        a.DeviceCount   = count;
        a.Status        = "Approved";
        await db.SaveChangesAsync();
    }

    public async Task RecordDeviceEventAsync(DeviceChangedPayload p)
    {
        await using var db = await _db.CreateDbContextAsync();
        var agent = await db.Agents.FirstOrDefaultAsync(a =>
            a.MachineName == p.MachineName && a.InstitutionId == p.InstitutionId);

        // If agent not found, the heartbeat hasn't arrived yet — skip this event
        // (heartbeat auto-registers, next event will succeed)
        if (agent == null)
        {
            _logger.LogDebug("No agent record for {Machine} — skipping event (will register on next heartbeat)", p.MachineName);
            return;
        }

        var cat = await db.DeviceCategories.FirstOrDefaultAsync(c => c.Name == p.Category);
        db.DeviceEvents.Add(new DeviceEventEntity
        {
            AgentId = agent.Id, CategoryId = cat?.Id, MachineName = p.MachineName,
            EventType = p.EventType, DeviceId = p.DeviceId, FriendlyName = p.FriendlyName,
            Category = p.Category, Status = p.Status, Timestamp = p.Timestamp
        });
        await db.SaveChangesAsync();
    }

    public async Task<List<DeviceEventEntity>> GetEventsAsync(int count = 200, Guid? institutionId = null, Guid? agentId = null)
    {
        await using var db = await _db.CreateDbContextAsync();
        var q = db.DeviceEvents.Include(e => e.Agent).AsQueryable();
        if (institutionId.HasValue) q = q.Where(e => e.Agent.InstitutionId == institutionId.Value);
        if (agentId.HasValue)       q = q.Where(e => e.AgentId == agentId.Value);
        return await q.OrderByDescending(e => e.Timestamp).Take(count).ToListAsync();
    }

    public async Task MarkStaleOfflineAsync(TimeSpan staleness)
    {
        await using var db = await _db.CreateDbContextAsync();
        var cutoff = DateTime.UtcNow - staleness;
        var stale  = await db.Agents.Where(a => a.Status == "Approved" &&
            (a.LastHeartbeat == null || a.LastHeartbeat < cutoff)).ToListAsync();
        foreach (var a in stale) a.Status = "Offline";
        if (stale.Count > 0) await db.SaveChangesAsync();
    }

    // ── Institutions ──────────────────────────────────────────────────────
    public async Task<List<InstitutionEntity>> GetInstitutionsAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.Institutions.OrderBy(i => i.Name).ToListAsync();
    }

    public async Task<InstitutionEntity?> GetInstitutionAsync(Guid id)
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.Institutions.FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<InstitutionEntity> CreateInstitutionAsync(string name, string type, string email)
    {
        await using var db = await _db.CreateDbContextAsync();
        var slug = MakeSlug(name);
        var i = 1; var base_ = slug;
        while (await db.Institutions.AnyAsync(x => x.Slug == slug)) slug = $"{base_}-{i++}";
        var inst = new InstitutionEntity { Name = name, Slug = slug, Type = type, ContactEmail = email };
        db.Institutions.Add(inst); await db.SaveChangesAsync();
        return inst;
    }

    public async Task<string> GenerateProvisionTokenAsync(Guid institutionId, Guid? templateId)
    {
        await using var db = await _db.CreateDbContextAsync();
        if (!await db.Institutions.AnyAsync(i => i.Id == institutionId))
            throw new InvalidOperationException("Institution not found");
        return JwtHelper.GenerateAgentToken("__PROVISIONED__", "__PENDING__", institutionId, JwtSecret);
    }

    // ── AgentTemplates ────────────────────────────────────────────────────
    public async Task<List<AgentTemplateEntity>> GetTemplatesAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.AgentTemplates.OrderBy(t => t.Name).ToListAsync();
    }

    public async Task<AgentTemplateEntity> CreateTemplateAsync(string name, string desc)
    {
        await using var db = await _db.CreateDbContextAsync();
        var t = new AgentTemplateEntity { Name = name, Description = desc };
        db.AgentTemplates.Add(t); await db.SaveChangesAsync(); return t;
    }

    // ── Categories ────────────────────────────────────────────────────────
    public async Task<List<DeviceCategoryEntity>> GetCategoriesAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        return await db.DeviceCategories.OrderBy(c => c.Name).ToListAsync();
    }

    private static string MakeSlug(string name) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(
                name.ToLowerInvariant().Replace(" ", "-"), @"[^a-z0-9\-]", ""),
            @"-+", "-").Trim('-');
}
