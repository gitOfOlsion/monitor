using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeripheralMonitor.Server.Services;

namespace PeripheralMonitor.Server.Controllers;

// Internal API — only accessible within Docker network (Caddy doesn't expose /api/* directly to agents).
// Web tier handles user authentication via cookies; these endpoints serve the Web's ServerApiClient.

static class Caller
{
    public static Guid?   InstId(ClaimsPrincipal u) => Guid.TryParse(u.FindFirstValue("institution_id"), out var id) ? id : null;
    public static bool    IsSuperAdmin(ClaimsPrincipal u) => u.IsInRole("SuperAdmin");
    public static string  Name(ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.Name) ?? "unknown";
}

[ApiController, Route("api/agents"), AllowAnonymous]
public class AgentsController : ControllerBase
{
    private readonly AgentService _svc;
    public AgentsController(AgentService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _svc.GetAgentsAsync());

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id)
    {
        var (ok, token) = await _svc.ApproveAsync(id, "Admin", null);
        return ok ? Ok(new { token }) : NotFound();
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id)
        => await _svc.RejectAsync(id, null) ? Ok() : NotFound();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
        => await _svc.DeleteAsync(id, null) ? Ok() : NotFound();
}

[ApiController, Route("api/events"), AllowAnonymous]
public class EventsController : ControllerBase
{
    private readonly AgentService _svc;
    public EventsController(AgentService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int count = 200, [FromQuery] Guid? agentId = null)
        => Ok(await _svc.GetRecentEventsAsync(count, null, agentId));
}

[ApiController, Route("api/institutions"), AllowAnonymous]
public class InstitutionsController : ControllerBase
{
    private readonly AgentService _svc;
    public InstitutionsController(AgentService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _svc.GetAllInstitutionsAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInstRequest r)
        => Ok(await _svc.CreateInstitutionAsync(r.Name, r.Type, r.ContactEmail));

    [HttpGet("{id:guid}/provision-token")]
    public async Task<IActionResult> ProvisionToken(Guid id, [FromQuery] Guid? templateId)
        => Ok(new { token = await _svc.GenerateProvisionTokenAsync(id, templateId) });
}
public record CreateInstRequest(string Name, string Type, string ContactEmail);

[ApiController, Route("api/templates"), AllowAnonymous]
public class AgentTemplatesController : ControllerBase
{
    private readonly AgentService _svc;
    public AgentTemplatesController(AgentService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _svc.GetTemplatesAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTplRequest r)
        => Ok(await _svc.CreateTemplateAsync(r.Name, r.Description));
}
public record CreateTplRequest(string Name, string Description);

[ApiController, Route("api/categories"), AllowAnonymous]
public class CategoriesController : ControllerBase
{
    private readonly AgentService _svc;
    public CategoriesController(AgentService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _svc.GetCategoriesAsync());
}

// ── Agent package downloads ────────────────────────────────────────────────
[ApiController, Route("api/download"), AllowAnonymous]
public class DownloadController : ControllerBase
{
    private readonly AgentPackageService _packages;
    private readonly AgentService        _agents;
    private readonly IConfiguration      _config;

    public DownloadController(AgentPackageService packages, AgentService agents, IConfiguration config)
    {
        _packages = packages;
        _agents   = agents;
        _config   = config;
    }

    /// <summary>GET /api/download/{institutionId}/windows</summary>
    [HttpGet("{institutionId:guid}/{platform}")]
    public async Task<IActionResult> DownloadAgent(Guid institutionId, string platform)
    {
        platform = platform.ToLowerInvariant();
        if (platform != "windows" && platform != "linux")
            return BadRequest(new { error = "Platform must be 'windows' or 'linux'" });

        var inst = await _agents.GetInstitutionAsync(institutionId);
        if (inst == null) return NotFound(new { error = "Institution not found" });

        if (!_packages.IsPackageAvailable(platform))
            return NotFound(new { error = $"Agent package for '{platform}' is not built yet. Rebuild the server image." });

        var serverUrl = _config["PublicUrl"] ?? $"{Request.Scheme}://{Request.Host}";
        var zipBytes  = _packages.BuildPackage(platform, institutionId, serverUrl);
        var fileName  = $"peripheral-monitor-agent-{inst.Slug}-{platform}.zip";

        return File(zipBytes, "application/zip", fileName);
    }

    /// <summary>Check which platforms are available</summary>
    [HttpGet("platforms")]
    public IActionResult GetPlatforms() => Ok(new
    {
        windows = _packages.IsPackageAvailable("windows"),
        linux   = _packages.IsPackageAvailable("linux")
    });
}
