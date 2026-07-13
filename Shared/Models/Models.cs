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
    string        Platform,
    DateTime      Timestamp,
    int           DeviceCount,
    string?       LoggedInUser = null,
    string?       CpuName      = null,
    int?          CpuCores     = null,
    long?         RamMb        = null,
    string?       DiskInfo     = null,
    double?       CpuPercent   = null,   // live CPU utilisation 0-100
    double?       RamPercent   = null,   // live RAM used 0-100
    double?       DiskPercent  = null,   // system-disk SPACE used 0-100
    long?         RamUsedMb    = null,   // live RAM used in MB (for display)
    double?       DiskActivePercent = null, // live disk ACTIVITY 0-100 (Task Manager "Disk %")
    double?       NetworkKbps  = null    // live network throughput in KB/s (all adapters)
);

public record DeviceChangedPayload(
    string   MachineName,
    Guid     InstitutionId,
    string   EventType,
    string   DeviceId,
    string   FriendlyName,
    string   Category,
    string   Status,
    DateTime Timestamp,
    Guid     EventId = default   // client-generated stable id; enables server-side dedup on resync
);

public record DeviceInfo(
    string DeviceId,
    string FriendlyName,
    string Category,
    string Status
);

// ── Remote commands (Server -> Agent via SignalR) ───────────────────────────
public enum AgentCommandType { Restart, Shutdown, CancelShutdown }

public record AgentCommandPayload(
    string         CommandId,   // unique id for correlating the result
    AgentCommandType Type,
    int?           DelaySeconds // for restart/shutdown: grace period before action
);

public record AgentCommandResult(
    string  CommandId,
    bool    Success,
    string? Message
);

// ── File push/pull (Server <-> Agent via SignalR) ───────────────────────────
// All transfers are scoped to a fixed "shared" folder on the agent —
// never arbitrary paths. See CommandExecutor/FileShareService on the agent.

public record FilePushRequest(
    string CommandId,
    string FileName,     // name only — no path separators allowed
    string Base64Content // up to 50MB
);

public record FilePullRequest(
    string CommandId,
    string FileName       // name only — must exist in the shared folder
);

public record FilePullResult(
    string  CommandId,
    bool    Success,
    string? Message,
    string? FileName,
    string? Base64Content
);

public record FileDeleteRequest(
    string CommandId,
    string FileName       // name only — must exist in the shared folder
);

public record FileDeleteResult(
    string  CommandId,
    bool    Success,
    string? Message
);

public record SharedFileInfo(
    string   Name,
    long     SizeBytes,
    DateTime ModifiedAt
);

public record SharedFolderListing(
    string             CommandId,
    bool               Success,
    string?            Message,
    string?            FolderPath,
    List<SharedFileInfo>? Files
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
    public string?   LoggedInUser  { get; set; }
    public string?   Subgroup      { get; set; }
    public string?   CpuName       { get; set; }
    public int?      CpuCores      { get; set; }
    public long?     RamMb         { get; set; }
    public string?   DiskInfo      { get; set; }
    // Nested institution info (populated when Server includes it)
    public InstitutionDto? Institution { get; set; }
}

/// <summary>Live utilisation snapshot + short sparkline history for one agent.</summary>
public class AgentMetricsDto
{
    public Guid     AgentId      { get; set; }
    public string   MachineName  { get; set; } = "";
    public Guid     InstitutionId   { get; set; }
    public string   InstitutionName { get; set; } = "";
    public string?  Subgroup     { get; set; }
    public string   Platform     { get; set; } = "Unknown";
    public bool     Online       { get; set; }
    public string?  CpuName      { get; set; }
    public int?     CpuCores     { get; set; }
    public long?    RamMb        { get; set; }
    public string?  DiskInfo     { get; set; }
    public string?  LoggedInUser { get; set; }
    // Latest live values
    public double?  CpuPercent   { get; set; }
    public double?  RamPercent   { get; set; }
    public double?  DiskPercent  { get; set; }   // disk space used
    public long?    RamUsedMb    { get; set; }
    public double?  DiskActivePercent { get; set; } // disk activity (Task Manager "Disk %")
    public double?  NetworkKbps  { get; set; }   // network throughput KB/s
    public DateTime? LastHeartbeat { get; set; }
    // Sparkline history (oldest → newest), ~last 60 samples
    public List<double> CpuHistory { get; set; } = [];
    public List<double> RamHistory { get; set; } = [];
    public List<double> DiskHistory { get; set; } = [];
    public List<double> NetHistory { get; set; } = [];
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

public record UpdateUserRequest(
    string?  Email,
    string?  Password         // null/empty = leave unchanged
);

public record SetSubgroupRequest(
    string?  Subgroup          // null/empty clears the subgroup (device becomes Ungrouped)
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

// ── Guacamole remote desktop ────────────────────────────────────────────────
public record SetRdpCredentialsRequest(
    string Username,   // domain\user or user@domain
    string Password,
    string? Domain     // optional AD domain
);

public record RdpConnectionResponse(
    string GuacamoleUrl  // pre-built URL to open directly in a new tab
);

// ── File share DTOs (API response shapes) ──────────────────────────────────
public class SharedFileDto
{
    public string   Name       { get; set; } = "";
    public long     SizeBytes  { get; set; }
    public DateTime ModifiedAt { get; set; }
}

// ── Feature management ──────────────────────────────────────────────────────
// The feature CATALOG is static (defined in code) so the UI knows what exists.
// Flags only store overrides. Resolution: agent → institution → default.

/// <summary>Static definition of a manageable feature.</summary>
public record FeatureDefinition(
    string  Key,
    string  Name,
    string  Description,
    string  Icon,
    bool    DefaultEnabled,
    bool    HasConfig            // true if the feature carries extra JSON config
);

public static class FeatureCatalog
{
    public const string FileShare     = "file-share";
    public const string RemoteDesktop = "remote-desktop";
    public const string PowerControl  = "power-control";
    public const string AppInstall    = "app-install";
    public const string Reports       = "reports";

    public static readonly List<FeatureDefinition> All = new()
    {
        new(FileShare,     "File Share",            "Upload, download and delete files in the device's shared folder.", "📁", true,  false),
        new(RemoteDesktop, "Remote Desktop",        "Browser-based RDP/VNC access to the device.",                       "🖥️", true,  false),
        new(PowerControl,  "Restart / Shutdown",    "Remotely restart, shut down, or cancel a pending power action.",    "⏻", true,  false),
        new(Reports,       "Reports",               "Generate and print filtered event reports for devices.",            "📄", false, false),
        new(AppInstall,    "Application Control",   "Block users from installing or running disallowed file types (e.g. games, unapproved software) on the device.", "🛡️", false, true),
    };

    public static FeatureDefinition? Find(string key) => All.FirstOrDefault(f => f.Key == key);
}

/// <summary>Effective (resolved) state of one feature for a given scope.</summary>
public class FeatureStateDto
{
    public string  Key            { get; set; } = "";
    public string  Name           { get; set; } = "";
    public string  Description    { get; set; } = "";
    public string  Icon           { get; set; } = "";
    public bool    HasConfig      { get; set; }
    public bool    DefaultEnabled { get; set; }
    // Institution-level override (null = not set, inherits default)
    public bool?   InstitutionEnabled { get; set; }
    // Agent-level override (only populated when querying a specific agent)
    public bool?   AgentEnabled   { get; set; }
    // The effective value after resolution
    public bool    Effective      { get; set; }
    public string? ConfigJson     { get; set; }
}

public record SetFeatureFlagRequest(
    string  FeatureKey,
    string  Scope,         // "Institution" | "Agent"
    Guid    ScopeId,
    bool?   Enabled,       // null = clear the override (revert to inherited/default)
    string? ConfigJson
);

/// <summary>Config payload for the app-install / software-deployment feature.</summary>
/// <summary>
/// Application Control policy (block model). When the feature is enabled for a
/// device, the agent blocks execution of files matching these rules for the
/// logged-in user. Block wins; the allow list is an exception carve-out.
/// </summary>
public class AppControlPolicy
{
    public bool         Enabled           { get; set; }            // master switch (mirrors the feature flag)
    // Defaults are EMPTY: enabling the feature blocks nothing until an admin
    // explicitly configures rules. This ensures removing an item from the policy
    // truly stops blocking it (no hidden built-in defaults to fall back on).
    public List<string> BlockedExtensions { get; set; } = new();    // e.g. ".exe", ".msi"
    public List<string> BlockedNamePatterns { get; set; } = new();  // substrings, e.g. "steam", "epicgames"
    public List<string> AllowedNamePatterns { get; set; } = new();  // carve-outs that override a block
    public bool         BlockInstallers   { get; set; } = false;     // opt-in only
}

// Kept for backward compatibility with any stored config; superseded by AppControlPolicy.
public class AppInstallConfig
{
    public List<string> AllowedExtensions { get; set; } = new() { ".exe", ".msi" };
    public List<string> BlockedExtensions { get; set; } = new() { ".bat", ".cmd", ".ps1", ".vbs", ".scr", ".js" };
    public int          MaxSizeMb         { get; set; } = 200;
}

// ── Reporting ───────────────────────────────────────────────────────────────
public record ReportRequest(
    Guid?       InstitutionId,
    List<Guid>? AgentIds,
    DateTime?   FromUtc,
    DateTime?   ToUtc,
    string?     Category,
    string?     EventType,
    string?     Status,
    string?     MachineContains,
    string?     UserContains
);

public class ReportRowDto
{
    public DateTime Timestamp    { get; set; }
    public string   MachineName  { get; set; } = "";
    public string?  LoggedInUser { get; set; }
    public string   EventType    { get; set; } = "";
    public string   FriendlyName { get; set; } = "";
    public string   Category     { get; set; } = "";
    public string   Status       { get; set; } = "";
}

public class ReportResultDto
{
    public List<ReportRowDto> Rows { get; set; } = [];
    public int    TotalRows        { get; set; }
    public bool   Truncated        { get; set; }   // true if hit the 5000 cap
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
}
