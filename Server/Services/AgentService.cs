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

    /// <summary>Fetch an agent by ID, enforcing institution scope. Returns null if not found or out of scope.</summary>
    public async Task<AgentEntity?> GetAgentByIdAsync(Guid agentId, Guid? callerInst)
    {
        await using var db = await _db.CreateDbContextAsync();
        var a = await db.Agents.FindAsync(agentId);
        if (a == null) return null;
        if (callerInst.HasValue && a.InstitutionId != callerInst.Value) return null;
        return a;
    }

    public async Task<(Guid agentId, DeviceChangedPayload? loginEvent)> UpdateHeartbeatAsync(
        string machineName, string macAddress, string platform, Guid institutionId, DateTime ts, int count, string? loggedInUser = null)
    {
        await using var db = await _db.CreateDbContextAsync();
        var a = await db.Agents.FirstOrDefaultAsync(x => x.MachineName == machineName && x.InstitutionId == institutionId);

        // Auto-register: agent has a valid institution JWT but no DB record yet
        if (a == null)
        {
            if (!await db.Institutions.AnyAsync(i => i.Id == institutionId))
            {
                _log.LogWarning("Heartbeat from {Machine} references institution {Inst} which does not exist. " +
                    "This agent has a stale token — re-download and reinstall the agent from the current dashboard.",
                    machineName, institutionId);
                return (Guid.Empty, null);
            }

            a = new AgentEntity
            {
                InstitutionId = institutionId,
                MachineName   = machineName,
                IPAddress     = "remote",
                MacAddress    = macAddress ?? "unknown",
                Platform      = platform ?? "Unknown",
                FirstSeen     = DateTime.UtcNow,
                Status        = "Approved",
                ApprovedBy    = "auto-provision",
                ApprovedAt    = DateTime.UtcNow
            };
            db.Agents.Add(a);
            _log.LogInformation("Auto-registered agent: {Machine} ({Mac}) [{Platform}] for institution {Inst}",
                machineName, macAddress, platform, institutionId);
        }

        // Update platform if it was previously Unknown
        if (a.Platform == "Unknown" && !string.IsNullOrEmpty(platform) && platform != "Unknown")
            a.Platform = platform;
        if (a.MacAddress == "unknown" && !string.IsNullOrEmpty(macAddress) && macAddress != "unknown")
            a.MacAddress = macAddress;

        // Detect login/logout events by comparing previous vs current user
        DeviceChangedPayload? userEvent = null;
        var prevUser = a.LoggedInUser;
        if (loggedInUser != prevUser)
        {
            a.LoggedInUser = loggedInUser;
            if (!string.IsNullOrEmpty(loggedInUser) && string.IsNullOrEmpty(prevUser))
            {
                // User logged in
                userEvent = new DeviceChangedPayload(machineName, institutionId, "Connected",
                    "user-session", loggedInUser, "UserSession", "Active", ts);
            }
            else if (string.IsNullOrEmpty(loggedInUser) && !string.IsNullOrEmpty(prevUser))
            {
                // User logged out
                userEvent = new DeviceChangedPayload(machineName, institutionId, "Disconnected",
                    "user-session", prevUser!, "UserSession", "Ended", ts);
            }
            else if (!string.IsNullOrEmpty(loggedInUser) && !string.IsNullOrEmpty(prevUser))
            {
                // User switched (logout + login)
                userEvent = new DeviceChangedPayload(machineName, institutionId, "Connected",
                    "user-session", loggedInUser, "UserSession", "Switched", ts);
            }
            // Persist the login/logout event
            if (userEvent != null) await RecordDeviceEventAsync(userEvent);
        }

        a.LastHeartbeat = ts;
        a.DeviceCount   = count;
        a.Status        = "Approved";
        await db.SaveChangesAsync();
        return (a.Id, userEvent);
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
            _log.LogWarning("DROPPED event: no agent record found for MachineName={Machine} InstitutionId={Inst} (EventType={Type}, Device={Device}). " +
                "This event will NOT appear in the dashboard. If this keeps happening for a machine that " +
                "clearly has an agent installed, check for MachineName/InstitutionId mismatches between the " +
                "heartbeat and device-change payloads.",
                p.MachineName, p.InstitutionId, p.EventType, p.FriendlyName);
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

    // ── RDP Credentials (stored encrypted per institution) ─────────────────
    public async Task<(string Username, string Password, string? Domain)?> GetRdpCredentialsAsync(Guid institutionId)
    {
        await using var db = await _db.CreateDbContextAsync();
        var inst = await db.Institutions.FindAsync(institutionId);
        if (inst == null || string.IsNullOrEmpty(inst.RdpUsername) || string.IsNullOrEmpty(inst.RdpPasswordEncrypted))
            return null;
        var decrypted = CredentialEncryption.Decrypt(inst.RdpPasswordEncrypted);
        return (inst.RdpUsername, decrypted, inst.RdpDomain);
    }

    public async Task<bool> SetRdpCredentialsAsync(Guid institutionId, string username, string password, string? domain, Guid? callerInst)
    {
        await using var db = await _db.CreateDbContextAsync();
        var inst = await db.Institutions.FindAsync(institutionId);
        if (inst == null) return false;
        if (callerInst.HasValue && inst.Id != callerInst.Value) return false;
        inst.RdpUsername          = username;
        inst.RdpPasswordEncrypted = CredentialEncryption.Encrypt(password);
        inst.RdpDomain            = domain;
        await db.SaveChangesAsync();
        return true;
    }
}
