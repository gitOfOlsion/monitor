namespace PeripheralMonitor.Shared.Models;

public enum AgentStatus   { Pending, Approved, Rejected, Offline }
public enum AgentPlatform { Windows, Linux, Unknown }
public enum UserRole      { SuperAdmin, InstitutionAdmin, Viewer }

public record DiscoveryBroadcast(
    string        MachineName,
    string        IPAddress,
    string        MacAddress,
    AgentPlatform Platform,
    Guid          InstitutionId,
    DateTime      Timestamp
);

public record HeartbeatPayload(
    string        MachineName,
    string        MacAddress,
    Guid          InstitutionId,
    string        Platform,       // "Windows" or "Linux"
    DateTime      Timestamp,
    int           DeviceCount
);

public record DeviceChangedPayload(
    string   MachineName,
    Guid     InstitutionId,
    string   EventType,
    string   DeviceId,
    string   FriendlyName,
    string   Category,
    string   Status,
    DateTime Timestamp
);

public record DeviceInfo(
    string DeviceId,
    string FriendlyName,
    string Category,
    string Status
);

// ── API Response DTOs ─────────────────────────────────────────────────────
// Used by Web to deserialize Server API responses.
// Server serializes its EF entities → JSON → Web deserializes into these.

public class InstitutionDto
{
    public Guid     Id           { get; set; }
    public string   Name         { get; set; } = "";
    public string   Slug         { get; set; } = "";
    public string   Type         { get; set; } = "";
    public string   ContactEmail { get; set; } = "";
    public string?  LogoUrl      { get; set; }
    public DateTime CreatedAt    { get; set; }
}

public class AgentDto
{
    public Guid      Id            { get; set; }
    public Guid      InstitutionId { get; set; }
    public Guid?     TemplateId    { get; set; }
    public string    MachineName   { get; set; } = "";
    public string    IPAddress     { get; set; } = "";
    public string    MacAddress    { get; set; } = "";
    public string    Platform      { get; set; } = "Unknown";
    public string    Status        { get; set; } = "Pending";
    public string?   JwtToken      { get; set; }
    public int       DeviceCount   { get; set; }
    public DateTime  FirstSeen     { get; set; }
    public DateTime? LastHeartbeat  { get; set; }
    public string?   ApprovedBy    { get; set; }
    public DateTime? ApprovedAt    { get; set; }
    // Nested institution info (populated when Server includes it)
    public InstitutionDto? Institution { get; set; }
}

public class DeviceEventDto
{
    public long     Id           { get; set; }
    public Guid     AgentId      { get; set; }
    public int?     CategoryId   { get; set; }
    public string   MachineName  { get; set; } = "";
    public string   EventType    { get; set; } = "";
    public string   DeviceId     { get; set; } = "";
    public string   FriendlyName { get; set; } = "";
    public string   Category     { get; set; } = "";
    public string   Status       { get; set; } = "";
    public DateTime Timestamp    { get; set; }
}

public class AgentTemplateDto
{
    public Guid     Id          { get; set; }
    public string   Name        { get; set; } = "";
    public string   Description { get; set; } = "";
    public DateTime CreatedAt   { get; set; }
}

public class DeviceCategoryDto
{
    public int    Id          { get; set; }
    public string Name        { get; set; } = "";
    public string Icon        { get; set; } = "";
    public string Description { get; set; } = "";
}

// ── User management DTOs ──────────────────────────────────────────────────
public class UserDto
{
    public string   Id              { get; set; } = "";
    public string   UserName        { get; set; } = "";
    public string   Email           { get; set; } = "";
    public string   Role            { get; set; } = "Viewer";
    public Guid?    InstitutionId   { get; set; }
    public string?  InstitutionName { get; set; }
    public DateTime CreatedAt       { get; set; }
}

public record CreateUserRequest(
    string  UserName,
    string  Email,
    string  Password,
    string  Role,            // InstitutionAdmin | Viewer (SuperAdmin only via seed)
    Guid?   InstitutionId
);

// ── Auth DTOs (Web <-> Server) ────────────────────────────────────────────
public record ValidateLoginRequest(string UserName, string Password);

public record ValidateLoginResponse(
    bool    Success,
    string? UserId,
    string? UserName,
    string? Role,
    Guid?   InstitutionId,
    string? InstitutionName
);

// ── Audit DTOs ────────────────────────────────────────────────────────────
public class AuditLogDto
{
    public long     Id            { get; set; }
    public DateTime Timestamp     { get; set; }
    public string   Actor         { get; set; } = "";
    public string   ActorRole     { get; set; } = "";
    public Guid?    InstitutionId { get; set; }
    public string   Action        { get; set; } = "";
    public string   Target        { get; set; } = "";
    public string?  Details       { get; set; }
    public string?  IpAddress     { get; set; }
}

public record CreateAuditRequest(
    string  Actor,
    string  ActorRole,
    Guid?   InstitutionId,
    string  Action,
    string  Target,
    string? Details,
    string? IpAddress
);
