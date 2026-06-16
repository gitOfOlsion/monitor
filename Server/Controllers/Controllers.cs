using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using PeripheralMonitor.Server.Hubs;
using PeripheralMonitor.Server.Services;
using PeripheralMonitor.Shared.Models;

namespace PeripheralMonitor.Server.Controllers;

// Internal API — only reachable inside the Docker network (Caddy never exposes /api/* to the public).
// The Web tier authenticates the user via cookie, then forwards identity on every call via headers:
//   X-Actor          = username
//   X-Actor-Role     = SuperAdmin | InstitutionAdmin | Viewer
//   X-Institution-Id = the caller's institution (empty for SuperAdmin)

public readonly record struct CallerCtx(string Actor, string Role, Guid? InstitutionId, string? Ip)
{
    public bool  IsSuperAdmin => Role == "SuperAdmin";
    public Guid? Scope        => IsSuperAdmin ? null : InstitutionId;
}

public abstract class ScopedController : ControllerBase
{
    protected CallerCtx Caller()
    {
        var actor = Request.Headers["X-Actor"].FirstOrDefault() ?? "unknown";
        var role  = Request.Headers["X-Actor-Role"].FirstOrDefault() ?? "Viewer";
        Guid? inst = Guid.TryParse(Request.Headers["X-Institution-Id"].FirstOrDefault(), out var g) ? g : null;
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        return new CallerCtx(actor, role, inst, ip);
    }
}

// ── Agents ──────────────────────────────────────────────────────────────
[ApiController, Route("api/agents"), AllowAnonymous]
public class AgentsController : ScopedController
{
    private readonly AgentService _svc;
    private readonly AuditService _audit;
    private readonly AgentConnectionRegistry _connections;
    private readonly IHubContext<MonitorHub> _hub;
    private readonly IConfiguration _config;

    public AgentsController(AgentService svc, AuditService audit, AgentConnectionRegistry connections, IHubContext<MonitorHub> hub, IConfiguration config)
    { _svc = svc; _audit = audit; _connections = connections; _hub = hub; _config = config; }

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _svc.GetAgentsAsync(Caller().Scope));

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id)
    {
        var c = Caller();
        var (ok, token) = await _svc.ApproveAsync(id, c.Actor, c.Scope);
        if (ok) await _audit.LogAsync(c.Actor, c.Role, c.InstitutionId, "agent.approve", id.ToString(), null, c.Ip);
        return ok ? Ok(new { token }) : NotFound();
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id)
    {
        var c = Caller();
        var ok = await _svc.RejectAsync(id, c.Scope);
        if (ok) await _audit.LogAsync(c.Actor, c.Role, c.InstitutionId, "agent.reject", id.ToString(), null, c.Ip);
        return ok ? Ok() : NotFound();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var c = Caller();
        var ok = await _svc.DeleteAsync(id, c.Scope);
        if (ok) await _audit.LogAsync(c.Actor, c.Role, c.InstitutionId, "agent.delete", id.ToString(), null, c.Ip);
        return ok ? Ok() : NotFound();
    }

    /// <summary>
    /// Send a remote command (restart / shutdown / cancel-shutdown) to an online agent.
    /// Only SuperAdmin or the agent's own institution admins may issue commands.
    /// Viewers are forbidden (read-only role).
    /// </summary>
    [HttpPost("{id:guid}/command")]
    public async Task<IActionResult> SendCommand(Guid id, [FromBody] SendCommandRequest req)
    {
        var c = Caller();
        if (c.Role == "Viewer") return Forbid();

        var agent = await _svc.GetAgentByIdAsync(id, c.Scope);
        if (agent == null) return NotFound(new { error = "Agent not found or out of scope" });

        var connectionId = _connections.GetConnectionId(id);
        if (connectionId == null)
            return BadRequest(new { error = "Agent is offline — cannot send command" });

        if (!Enum.TryParse<AgentCommandType>(req.Type, ignoreCase: true, out var cmdType))
            return BadRequest(new { error = $"Unknown command type '{req.Type}'" });

        var commandId = Guid.NewGuid().ToString("N");
        var payload   = new AgentCommandPayload(commandId, cmdType, req.DelaySeconds);

        // Fire the command at the agent, then wait briefly for an ack/result.
        var waitTask = _connections.WaitForResult<AgentCommandResult>(commandId, TimeSpan.FromSeconds(15),
            () => new AgentCommandResult(commandId, false, "Timed out waiting for agent response"));
        await _hub.Clients.Client(connectionId).SendAsync("ExecuteCommand", payload);
        var result = await waitTask;

        await _audit.LogAsync(c.Actor, c.Role, c.InstitutionId, $"agent.command.{cmdType.ToString().ToLowerInvariant()}",
            agent.MachineName, result.Success ? "ok" : $"failed: {result.Message}", c.Ip);

        return Ok(new { success = result.Success, message = result.Message });
    }

    /// <summary>List files in the agent's shared folder.</summary>
    [HttpGet("{id:guid}/files")]
    public async Task<IActionResult> ListSharedFiles(Guid id)
    {
        var c = Caller();
        var agent = await _svc.GetAgentByIdAsync(id, c.Scope);
        if (agent == null) return NotFound(new { error = "Agent not found or out of scope" });

        var connectionId = _connections.GetConnectionId(id);
        if (connectionId == null) return BadRequest(new { error = "Agent is offline" });

        var commandId = Guid.NewGuid().ToString("N");
        var waitTask = _connections.WaitForResult<SharedFolderListing>(commandId, TimeSpan.FromSeconds(15),
            () => new SharedFolderListing(commandId, false, "Timed out waiting for agent response", null, null));
        await _hub.Clients.Client(connectionId).SendAsync("ListSharedFolder", commandId);
        var listing = await waitTask;

        if (!listing.Success) return BadRequest(new { error = listing.Message });
        return Ok(new { folderPath = listing.FolderPath, files = listing.Files ?? [] });
    }

    /// <summary>Push a file to the agent's shared folder. Max 50MB, filename only (no paths).</summary>
    [HttpPost("{id:guid}/files/push")]
    public async Task<IActionResult> PushFile(Guid id, [FromBody] PushFileRequest req)
    {
        var c = Caller();
        if (c.Role == "Viewer") return Forbid();

        var agent = await _svc.GetAgentByIdAsync(id, c.Scope);
        if (agent == null) return NotFound(new { error = "Agent not found or out of scope" });

        var connectionId = _connections.GetConnectionId(id);
        if (connectionId == null) return BadRequest(new { error = "Agent is offline" });

        if (string.IsNullOrWhiteSpace(req.FileName) || req.FileName.Contains('/') || req.FileName.Contains('\\') || req.FileName.Contains(".."))
            return BadRequest(new { error = "Invalid file name" });

        var commandId = Guid.NewGuid().ToString("N");
        var payload   = new FilePushRequest(commandId, req.FileName, req.Base64Content);

        var waitTask = _connections.WaitForResult<AgentCommandResult>(commandId, TimeSpan.FromSeconds(60),
            () => new AgentCommandResult(commandId, false, "Timed out waiting for agent response"));
        await _hub.Clients.Client(connectionId).SendAsync("PushFile", payload);
        var result = await waitTask;

        await _audit.LogAsync(c.Actor, c.Role, c.InstitutionId, "agent.file.push",
            $"{agent.MachineName}:{req.FileName}", result.Success ? "ok" : $"failed: {result.Message}", c.Ip);

        return Ok(new { success = result.Success, message = result.Message });
    }

    /// <summary>Pull a file from the agent's shared folder. Filename only (no paths), max 50MB.</summary>
    [HttpGet("{id:guid}/files/pull")]
    public async Task<IActionResult> PullFile(Guid id, [FromQuery] string fileName)
    {
        var c = Caller();
        if (c.Role == "Viewer") return Forbid();

        var agent = await _svc.GetAgentByIdAsync(id, c.Scope);
        if (agent == null) return NotFound(new { error = "Agent not found or out of scope" });

        var connectionId = _connections.GetConnectionId(id);
        if (connectionId == null) return BadRequest(new { error = "Agent is offline" });

        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
            return BadRequest(new { error = "Invalid file name" });

        var commandId = Guid.NewGuid().ToString("N");
        var payload   = new FilePullRequest(commandId, fileName);

        var waitTask = _connections.WaitForResult<FilePullResult>(commandId, TimeSpan.FromSeconds(60),
            () => new FilePullResult(commandId, false, "Timed out waiting for agent response", null, null));
        await _hub.Clients.Client(connectionId).SendAsync("PullFile", payload);
        var result = await waitTask;

        await _audit.LogAsync(c.Actor, c.Role, c.InstitutionId, "agent.file.pull",
            $"{agent.MachineName}:{fileName}", result.Success ? "ok" : $"failed: {result.Message}", c.Ip);

        if (!result.Success) return BadRequest(new { error = result.Message });

        var bytes = Convert.FromBase64String(result.Base64Content!);
        return File(bytes, "application/octet-stream", result.FileName);
    }

    /// <summary>
    /// Returns a rustdesk:// URI for connecting to this agent's machine.
    /// Requires RustDesk to be installed (done automatically by the agent installer)
    /// and the agent to have reported its RustDesk ID via heartbeat.
    /// Viewers cannot retrieve this (it includes the shared connection password).
    /// </summary>
    [HttpGet("{id:guid}/remote-desktop")]
    public async Task<IActionResult> GetRemoteDesktopLink(Guid id)
    {
        var c = Caller();
        if (c.Role == "Viewer") return Forbid();

        var agent = await _svc.GetAgentByIdAsync(id, c.Scope);
        if (agent == null) return NotFound(new { error = "Agent not found or out of scope" });

        if (string.IsNullOrEmpty(agent.RustDeskId))
            return NotFound(new { error = "RustDesk ID not available yet. The agent may still be starting RustDesk, or RustDesk could not be installed on this machine (e.g. headless server)." });

        var password = _config["RustDesk:Password"] ?? "";
        var uri = $"rustdesk://connect/{agent.RustDeskId}?password={Uri.EscapeDataString(password)}";

        await _audit.LogAsync(c.Actor, c.Role, c.InstitutionId, "agent.remote-desktop.connect", agent.MachineName, null, c.Ip);

        return Ok(new { rustDeskId = agent.RustDeskId, password, uri });
    }
}
public record PushFileRequest(string FileName, string Base64Content);
public record SendCommandRequest(string Type, int? DelaySeconds);

// ── Events ────────────────────────────────────────────────────────────────
[ApiController, Route("api/events"), AllowAnonymous]
public class EventsController : ScopedController
{
    private readonly AgentService _svc;
    public EventsController(AgentService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int count = 200, [FromQuery] Guid? agentId = null)
        => Ok(await _svc.GetEventsAsync(count, Caller().Scope, agentId));
}

// ── Institutions ────────────────────────────────────────────────────────────
[ApiController, Route("api/institutions"), AllowAnonymous]
public class InstitutionsController : ScopedController
{
    private readonly AgentService _svc;
    private readonly AuditService _audit;
    public InstitutionsController(AgentService svc, AuditService audit) { _svc = svc; _audit = audit; }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var c = Caller();
        var all = await _svc.GetInstitutionsAsync();
        if (!c.IsSuperAdmin && c.InstitutionId.HasValue)
            all = all.Where(i => i.Id == c.InstitutionId.Value).ToList();
        return Ok(all);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInstRequest r)
    {
        var c = Caller();
        if (!c.IsSuperAdmin) return Forbid();
        var inst = await _svc.CreateInstitutionAsync(r.Name, r.Type, r.ContactEmail);
        await _audit.LogAsync(c.Actor, c.Role, null, "institution.create", inst.Name, null, c.Ip);
        return Ok(inst);
    }

    [HttpGet("{id:guid}/provision-token")]
    public async Task<IActionResult> ProvisionToken(Guid id, [FromQuery] Guid? templateId)
    {
        var c = Caller();
        if (!c.IsSuperAdmin && c.InstitutionId != id) return Forbid();
        return Ok(new { token = await _svc.GenerateProvisionTokenAsync(id, templateId) });
    }
}
public record CreateInstRequest(string Name, string Type, string ContactEmail);

// ── Users ───────────────────────────────────────────────────────────────────
[ApiController, Route("api/users"), AllowAnonymous]
public class UsersController : ScopedController
{
    private readonly UserService  _users;
    private readonly AuditService _audit;
    public UsersController(UserService users, AuditService audit) { _users = users; _audit = audit; }

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _users.GetUsersAsync(Caller().Scope));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req)
    {
        var c = Caller();
        if (c.Role != "SuperAdmin" && c.Role != "InstitutionAdmin") return Forbid();
        var (ok, error) = await _users.CreateUserAsync(req, c.Role, c.InstitutionId);
        if (!ok) return BadRequest(new { error });
        await _audit.LogAsync(c.Actor, c.Role, c.InstitutionId, "user.create", req.UserName, $"role={req.Role}", c.Ip);
        return Ok();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var c = Caller();
        if (c.Role != "SuperAdmin" && c.Role != "InstitutionAdmin") return Forbid();
        var (ok, error) = await _users.DeleteUserAsync(id, c.Role, c.InstitutionId);
        if (!ok) return BadRequest(new { error });
        await _audit.LogAsync(c.Actor, c.Role, c.InstitutionId, "user.delete", id, null, c.Ip);
        return Ok();
    }
}

// ── Auth validation (called by Web on login) ────────────────────────────────
[ApiController, Route("api/auth"), AllowAnonymous]
public class AuthValidationController : ScopedController
{
    private readonly UserService  _users;
    private readonly AuditService _audit;
    public AuthValidationController(UserService users, AuditService audit) { _users = users; _audit = audit; }

    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] ValidateLoginRequest req)
    {
        var result = await _users.ValidateAsync(req.UserName, req.Password);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (result.Success)
            await _audit.LogAsync(result.UserName!, result.Role!, result.InstitutionId, "auth.login", result.UserName!, null, ip);
        else
            await _audit.LogAsync(req.UserName, "?", null, "auth.login_failed", req.UserName, null, ip);
        return Ok(result);
    }
}

// ── Audit log ────────────────────────────────────────────────────────────────
[ApiController, Route("api/audit"), AllowAnonymous]
public class AuditController : ScopedController
{
    private readonly AuditService _audit;
    public AuditController(AuditService audit) => _audit = audit;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int count = 500)
        => Ok(await _audit.GetLogsAsync(count, Caller().Scope));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAuditRequest req)
    {
        await _audit.LogAsync(req);
        return Ok();
    }
}

// ── Templates ────────────────────────────────────────────────────────────────
[ApiController, Route("api/templates"), AllowAnonymous]
public class AgentTemplatesController : ScopedController
{
    private readonly AgentService _svc;
    public AgentTemplatesController(AgentService svc) => _svc = svc;

    [HttpGet] public async Task<IActionResult> GetAll() => Ok(await _svc.GetTemplatesAsync());
    [HttpPost] public async Task<IActionResult> Create([FromBody] CreateTplRequest r)
        => Ok(await _svc.CreateTemplateAsync(r.Name, r.Description));
}
public record CreateTplRequest(string Name, string Description);

// ── Device categories ────────────────────────────────────────────────────────
[ApiController, Route("api/categories"), AllowAnonymous]
public class CategoriesController : ScopedController
{
    private readonly AgentService _svc;
    public CategoriesController(AgentService svc) => _svc = svc;
    [HttpGet] public async Task<IActionResult> GetAll() => Ok(await _svc.GetCategoriesAsync());
}

// ── Agent package downloads ────────────────────────────────────────────────
[ApiController, Route("api/download"), AllowAnonymous]
public class DownloadController : ScopedController
{
    private readonly AgentPackageService _packages;
    private readonly AgentService        _agents;
    private readonly IConfiguration      _config;
    public DownloadController(AgentPackageService packages, AgentService agents, IConfiguration config)
    { _packages = packages; _agents = agents; _config = config; }

    [HttpGet("{institutionId:guid}/{platform}")]
    public async Task<IActionResult> DownloadAgent(Guid institutionId, string platform)
    {
        platform = platform.ToLowerInvariant();
        if (platform != "windows" && platform != "linux")
            return BadRequest(new { error = "Platform must be 'windows' or 'linux'" });

        var inst = await _agents.GetInstitutionAsync(institutionId);
        if (inst == null) return NotFound(new { error = "Institution not found" });

        if (!_packages.IsPackageAvailable(platform))
            return NotFound(new { error = $"Agent package for '{platform}' is not built yet." });

        var serverUrl = _config["PublicUrl"] ?? $"{Request.Scheme}://{Request.Host}";
        var zipBytes  = _packages.BuildPackage(platform, institutionId, serverUrl);
        return File(zipBytes, "application/zip",
            $"peripheral-monitor-agent-{inst.Slug}-{platform}.zip");
    }

    [HttpGet("platforms")]
    public IActionResult GetPlatforms() => Ok(new
    {
        windows = _packages.IsPackageAvailable("windows"),
        linux   = _packages.IsPackageAvailable("linux")
    });

    /// <summary>
    /// Download the one-time RustDesk configurator for admins. This is NOT the agent —
    /// it just points the admin's own RustDesk client at this deployment's relay server,
    /// so "Remote Desktop" links from the dashboard connect correctly. Run once per admin machine.
    /// </summary>
    [HttpGet("rustdesk-setup/{platform}")]
    public IActionResult DownloadRustDeskSetup(string platform)
    {
        platform = platform.ToLowerInvariant();
        if (platform != "windows" && platform != "linux")
            return BadRequest(new { error = "Platform must be 'windows' or 'linux'" });

        var fileName = platform == "windows" ? "configure-admin-rustdesk.ps1" : "configure-admin-rustdesk.sh";
        var path = Path.Combine("/app/agent-packages/installers", fileName);
        if (!System.IO.File.Exists(path))
            return NotFound(new { error = "Configurator script not found. Rebuild the server image." });

        var bytes = System.IO.File.ReadAllBytes(path);
        return File(bytes, "text/plain", fileName);
    }
}
