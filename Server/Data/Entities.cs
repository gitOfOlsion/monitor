using Microsoft.AspNetCore.Identity;

namespace PeripheralMonitor.Server.Data;

// ════════════════════════════════════════════════════════════════════════
// INSTITUTION
// ════════════════════════════════════════════════════════════════════════
public class Institution
{
    public Guid     Id           { get; set; } = Guid.NewGuid();
    public string   Name         { get; set; } = "";
    public string   Slug         { get; set; } = "";  // unique URL-safe identifier
    public string   Type         { get; set; } = "";  // School, University, Company…
    public string   ContactEmail { get; set; } = "";
    public string?  LogoUrl      { get; set; }
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;

    public ICollection<User>   Users  { get; set; } = [];
    public ICollection<Agent>  Agents { get; set; } = [];
}

// ════════════════════════════════════════════════════════════════════════
// USER  (extends IdentityUser — table name: Users)
// ════════════════════════════════════════════════════════════════════════
public class User : IdentityUser
{
    public Guid?    InstitutionId { get; set; }   // null = SuperAdmin (no institution)
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;

    public Institution? Institution { get; set; }
}

// ════════════════════════════════════════════════════════════════════════
// AGENT TEMPLATE
// No FK to Institution — linked only via Agent.TemplateId
// A template holds the pre-generated JWT used during provisioning.
// The institution_id is encoded INSIDE the JWT payload itself.
// ════════════════════════════════════════════════════════════════════════
public class AgentTemplate
{
    public Guid      Id          { get; set; } = Guid.NewGuid();
    public string    Name        { get; set; } = "";
    public string?   Description { get; set; }
    public string    JwtToken    { get; set; } = "";  // template JWT (institution_id in claims)
    public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt   { get; set; }

    public ICollection<Agent> Agents { get; set; } = [];
}

// ════════════════════════════════════════════════════════════════════════
// AGENT
// ════════════════════════════════════════════════════════════════════════
public class Agent
{
    public Guid      Id            { get; set; } = Guid.NewGuid();
    public Guid      InstitutionId { get; set; }
    public Guid?     TemplateId    { get; set; }  // which template provisioned this agent
    public string    MachineName   { get; set; } = "";
    public string    IPAddress     { get; set; } = "";
    public string    MacAddress    { get; set; } = "";
    public string    Platform      { get; set; } = "Unknown";  // Windows / Linux
    public string    Status        { get; set; } = "Pending";  // Pending/Approved/Rejected/Offline
    public string?   JwtToken      { get; set; }               // per-machine token (post-approval)
    public int       DeviceCount   { get; set; }
    public DateTime  FirstSeen     { get; set; } = DateTime.UtcNow;
    public DateTime? LastHeartbeat { get; set; }
    public string?   ApprovedBy    { get; set; }
    public DateTime? ApprovedAt    { get; set; }

    public Institution              Institution  { get; set; } = null!;
    public AgentTemplate?           Template     { get; set; }
    public ICollection<DeviceEvent> DeviceEvents { get; set; } = [];
}

// ════════════════════════════════════════════════════════════════════════
// DEVICE CATEGORY  (lookup / seed table)
// ════════════════════════════════════════════════════════════════════════
public class DeviceCategory
{
    public int     Id          { get; set; }
    public string  Name        { get; set; } = "";  // unique
    public string  Icon        { get; set; } = "🔧";
    public string? Description { get; set; }

    public ICollection<DeviceEvent> DeviceEvents { get; set; } = [];
}

// ════════════════════════════════════════════════════════════════════════
// DEVICE EVENT
// ════════════════════════════════════════════════════════════════════════
public class DeviceEvent
{
    public long     Id           { get; set; }
    public Guid     AgentId      { get; set; }
    public int?     CategoryId   { get; set; }
    public string   MachineName  { get; set; } = "";
    public string   DeviceId     { get; set; } = "";
    public string   FriendlyName { get; set; } = "";
    public string   EventType    { get; set; } = "";  // Connected / Disconnected / Modified
    public string   Status       { get; set; } = "";
    public DateTime Timestamp    { get; set; } = DateTime.UtcNow;

    public Agent          Agent    { get; set; } = null!;
    public DeviceCategory? Category { get; set; }
}
